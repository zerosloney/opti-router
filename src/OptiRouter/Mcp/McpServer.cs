using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP 服务器主机（JSON-RPC 2.0 over HTTP）。
/// 实现 MCP 2025-03-26 规范的服务器侧：暴露 OptiRouter 内置工具，
/// 接收外部客户端（如 Claude Code、Cline、Cursor 等 MCP 客户端）的调用。
///
/// 与现有的 <see cref="McpToolOrchestrator"/>（MCP Client，调外部工具）互补：
/// - Client 侧：OptiRouter 作为代理，帮用户调用远程 MCP 工具
/// - Server 侧：OptiRouter 作为 MCP Server，暴露本地能力给外部 agent
/// </summary>
public sealed class McpServerHost
{
    private const string JsonRpcVersion = "2.0";
    private const string ProtocolVersion = "2025-03-26";

    private readonly McpServerToolProvider _toolProvider;
    private readonly McpServerOptions _options;
    private readonly ILogger<McpServerHost> _logger;

    public McpServerHost(McpServerOptions options, McpServerToolProvider toolProvider, ILogger<McpServerHost>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _toolProvider = toolProvider ?? throw new ArgumentNullException(nameof(toolProvider));
        _logger = logger ?? NullLogger<McpServerHost>.Instance;
    }

    /// <summary>
    /// 处理 MCP HTTP POST 请求（JSON-RPC 2.0 单请求或 batch）。
    /// </summary>
    public async Task<McpServerResponse> HandleRequestAsync(string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return McpServerResponse.Error(-32600, "Invalid Request: empty body");
        }

        try
        {
            // 尝试解析为单个请求
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Batch 请求
                var responses = new List<JsonElement>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var result = await HandleSingleMessageAsync(item, ct);
                    if (result.HasValue)
                        responses.Add(result.Value);
                }
                return McpServerResponse.Batch(responses);
            }
            else
            {
                // 单个请求
                var result = await HandleSingleMessageAsync(doc.RootElement, ct);
                return result.HasValue
                    ? McpServerResponse.Single(result.Value)
                    : McpServerResponse.Error(-32600, "Invalid Request");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "MCP server JSON parse error");
            return McpServerResponse.Error(-32700, "Parse error");
        }
    }

    /// <summary>
    /// 处理单个 JSON-RPC 消息。协议级错误（未知 method / 参数缺失）按 JSON-RPC 2.0
    /// 形状写入 <c>error</c> 字段，不得包进 <c>result</c>——严格客户端会把后者当成功结果。
    /// </summary>
    private async Task<JsonElement?> HandleSingleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        if (!msg.TryGetProperty("jsonrpc", out var rpcVersion) || !rpcVersion.ValueEquals(JsonRpcVersion))
            return null;

        string? method = null;
        JsonElement? id = null;
        JsonElement? @params = null;

        if (msg.TryGetProperty("method", out var m)) method = m.GetString();
        if (msg.TryGetProperty("id", out var i)) id = i;
        if (msg.TryGetProperty("params", out var p)) @params = p;

        if (string.IsNullOrWhiteSpace(method))
            return MakeError(id, -32601, "Method not found");

        var outcome = method switch
        {
            "initialize" => RpcOutcome.Ok(HandleInitialize()),
            "ping" => RpcOutcome.Ok(HandlePing()),
            "tools/list" => RpcOutcome.Ok(await HandleToolsListAsync(@params, ct).ConfigureAwait(false)),
            "tools/call" => await HandleToolsCallAsync(@params, ct).ConfigureAwait(false),
            "shutdown" => RpcOutcome.Ok(HandleShutdown()),
            "notifications/initialized" => null, // 无响应通知
            "notifications/cancelled" => null,
            _ => HandleUnknownMethod(method)
        };

        // 通知（无 id）不产生响应
        if (outcome is null || !id.HasValue || id.Value.ValueKind == JsonValueKind.Null)
            return null;

        return outcome.ErrorCode is int code
            ? MakeError(id.Value, code, outcome.ErrorMessage ?? "Error")
            : MakeResponse(id.Value, outcome.Result);
    }

    /// <summary>
    /// initialize — MCP 协议握手。
    /// </summary>
    private JsonElement HandleInitialize()
    {
        _logger.LogInformation("MCP client initialized connection");

        return JsonSerializer.SerializeToElement(new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new
            {
                tools = new { },
                logging = new { },
                sampling = new { }
            },
            serverInfo = new
            {
                name = "optirouter",
                version = "1.0"
            }
        });
    }

    /// <summary>
    /// ping — 心跳保活。
    /// </summary>
    private JsonElement HandlePing()
    {
        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>
    /// tools/list — 列出所有可用工具。
    /// </summary>
    private async Task<JsonElement> HandleToolsListAsync(JsonElement? @params, CancellationToken ct)
    {
        var tools = await _toolProvider.GetToolsAsync(ct);
        var toolList = tools.Select(t => new
        {
            name = t.Name,
            description = t.Description ?? "",
            inputSchema = t.InputSchema ?? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
        }).ToList();

        return JsonSerializer.SerializeToElement(new { tools = toolList });
    }

    /// <summary>
    /// tools/call — 调用指定工具。工具执行受 <see cref="McpServerOptions.MaxToolCallTimeoutMs"/>
    /// 超时兜底：超时返回 isError 内容（响应 token 的工具被取消，不响应的经 WaitAsync 竞速掐断），
    /// 防止单个慢工具挂死 MCP 客户端请求。协议级参数错误按 -32602 返回。
    /// </summary>
    private async Task<RpcOutcome> HandleToolsCallAsync(JsonElement? @params, CancellationToken ct)
    {
        if (@params is null)
            return RpcOutcome.Error(-32602, "Invalid params: missing parameters");

        string? toolName = null;
        JsonElement? arguments = null;

        if (@params.Value.TryGetProperty("name", out var nameEl))
            toolName = nameEl.GetString();
        if (@params.Value.TryGetProperty("arguments", out var argsEl))
            arguments = argsEl;

        if (string.IsNullOrWhiteSpace(toolName))
            return RpcOutcome.Error(-32602, "Invalid params: tool name is required");

        int timeoutMs = Math.Max(1_000, _options.MaxToolCallTimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        bool success;
        string? content, error;
        var execTask = _toolProvider.ExecuteToolAsync(toolName, arguments ?? default, timeoutCts.Token);
        try
        {
            (success, content, error) = await execTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 客户端未断开而取消来自超时：返回超时错误内容（客户端断开的取消继续向上传播）
            _logger.LogWarning("MCP tool {Tool} timed out after {TimeoutMs}ms", toolName, timeoutMs);
            return RpcOutcome.Ok(MakeToolContent($"Tool execution timed out after {timeoutMs}ms", isError: true));
        }

        return RpcOutcome.Ok(MakeToolContent(
            success ? content ?? "" : error ?? "Tool execution failed",
            isError: !success));
    }

    private JsonElement MakeToolContent(string text, bool isError) =>
        JsonSerializer.SerializeToElement(new
        {
            content = new object[] { new { type = "text", text } },
            isError
        });

    /// <summary>
    /// shutdown — 优雅关闭。
    /// </summary>
    private JsonElement HandleShutdown()
    {
        _logger.LogInformation("MCP server shutting down");
        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>
    /// 未知 method — JSON-RPC -32601。
    /// </summary>
    private RpcOutcome HandleUnknownMethod(string method)
    {
        _logger.LogDebug("MCP server received unknown method: {Method}", method);
        return RpcOutcome.Error(-32601, $"Method not found: {method}");
    }

    /// <summary>
    /// 单个 method 的执行结果：成功携带 result，失败携带 JSON-RPC error code/message。
    /// </summary>
    private sealed record RpcOutcome(int? ErrorCode, string? ErrorMessage, JsonElement Result)
    {
        public static RpcOutcome Ok(JsonElement result) => new(null, null, result);
        public static RpcOutcome Error(int code, string message) => new(code, message, default);
    }

    private static JsonElement MakeResponse(JsonElement id, JsonElement result)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            id,
            result
        }));
        return doc.RootElement.Clone();
    }

    private static JsonElement? MakeError(JsonElement? id, int code, string message)
    {
        if (!id.HasValue || id.Value.ValueKind == JsonValueKind.Null)
            return null;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            jsonrpc = JsonRpcVersion,
            id = id.Value,
            error = new { code, message }
        }));
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// MCP 服务器配置。
/// </summary>
public sealed class McpServerOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "/mcp";
    public int MaxToolCallTimeoutMs { get; set; } = 30_000;
}

/// <summary>
/// MCP 服务器工具提供者接口。
/// </summary>
public interface McpServerToolProvider
{
    Task<IReadOnlyList<McpServerTool>> GetToolsAsync(CancellationToken ct = default);
    Task<(bool Success, string? Content, string? Error)> ExecuteToolAsync(string toolName, JsonElement? arguments, CancellationToken ct = default);
}

/// <summary>
/// MCP 服务器暴露的单个工具。
/// </summary>
public sealed record McpServerTool(
    string Name,
    string? Description,
    JsonElement? InputSchema);

/// <summary>
/// MCP 服务器响应。
/// </summary>
public abstract record McpServerResponse
{
    public static McpServerResponse Single(JsonElement body) => new SingleResponse { Content = body };
    public static McpServerResponse Batch(IList<JsonElement> bodies) => new BatchResponse { Content = bodies };
    public static McpServerResponse Error(int code, string message) => new ErrorResponse(code, message);
    public abstract int StatusCode { get; }
    public abstract string Body { get; }
}

public sealed record SingleResponse : McpServerResponse
{
    public required JsonElement Content { get; init; }
    public override int StatusCode => 200;
    public override string Body => JsonSerializer.Serialize(Content);
}

public sealed record BatchResponse : McpServerResponse
{
    public required IList<JsonElement> Content { get; init; }
    public override int StatusCode => 200;
    public override string Body => JsonSerializer.Serialize(Content);
}

public sealed record ErrorResponse(int Code, string Message) : McpServerResponse
{
    public override int StatusCode => 200; // JSON-RPC 错误也在 200 里
    public override string Body => JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = Code, message = Message } });
}
