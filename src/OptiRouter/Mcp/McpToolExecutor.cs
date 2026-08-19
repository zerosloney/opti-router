using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP 工具执行结果。
/// </summary>
/// <param name="IsSuccess">工具调用是否成功（JSON-RPC 无 error 且 result.isError 为 false）。</param>
/// <param name="Content">执行结果文本（拼接 result.content 中全部 text 类型片段）。</param>
/// <param name="ErrorMessage">失败原因。</param>
public sealed record McpToolCallResult(bool IsSuccess, string Content, string? ErrorMessage = null);

/// <summary>
/// 待执行的工具调用（从模型响应 JSON 解析）。
/// </summary>
public sealed record McpPendingToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// MCP 工具执行契约。
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// 对指定 MCP Server 执行一次工具调用（Streamable HTTP / JSON-RPC 2.0）。
    /// </summary>
    Task<McpToolCallResult> ExecuteToolAsync(McpServerRegistration server, string toolName, JsonElement arguments, CancellationToken ct = default);
}

/// <summary>
/// MCP Streamable HTTP 传输工具执行器 (MCP Tool Executor)。
/// 按 MCP 2025-03-26 规范完成 initialize 握手 → initialized 通知 → tools/call 调用，
/// 复用服务端返回的 Mcp-Session-Id 会话头；结果拼接 content 文本并区分 isError。
/// </summary>
public sealed class McpToolExecutor : IMcpToolExecutor
{
    private const string ProtocolVersion = "2025-03-26";
    private const string JsonRpcVersion = "2.0";
    private const string SessionHeader = "Mcp-Session-Id";

    private readonly HttpClient _httpClient;
    private readonly ILogger<McpToolExecutor> _logger;
    private int _requestId;

    public McpToolExecutor(HttpClient httpClient, ILogger<McpToolExecutor>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<McpToolExecutor>.Instance;
    }

    /// <inheritdoc />
    public async Task<McpToolCallResult> ExecuteToolAsync(
        McpServerRegistration server,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new McpToolCallResult(false, string.Empty, "Tool name cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            return new McpToolCallResult(false, string.Empty, "MCP server BaseUrl cannot be empty.");
        }

        // Undefined/Null 参数归一化为空对象（default(JsonElement) 无法直接序列化）。
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            arguments = JsonSerializer.SerializeToElement(new { });
        }

        string endpoint = server.BaseUrl.TrimEnd('/');
        int timeoutMs = server.TimeoutMs > 0 ? server.TimeoutMs : 15000;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var effectiveCt = timeoutCts.Token;

        try
        {
            // 1. initialize 握手（获取可选的服务端会话 ID）
            string? sessionId = null;
            using (var initResponse = await SendJsonRpcAsync(
                endpoint, "initialize", server,
                new { protocolVersion = ProtocolVersion, capabilities = new { }, clientInfo = new { name = "OptiRouter", version = "1.0" } },
                sessionId, effectiveCt).ConfigureAwait(false))
            {
                string initBody = await OptiRouter.Clients.BoundedResponseReader.ReadBodyAsync(initResponse.Content, effectiveCt).ConfigureAwait(false);
                var (initOk, initError) = ParseRpcResult(initBody);
                if (!initOk)
                {
                    return new McpToolCallResult(false, string.Empty, $"MCP initialize failed: {initError}");
                }
                sessionId = initResponse.Headers.TryGetValues(SessionHeader, out var sessionValues)
                    ? sessionValues.FirstOrDefault()
                    : null;
            }

            // 2. initialized 通知（服务端下发会话 ID 时按规范发送）
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                using var notif = await SendJsonRpcAsync(endpoint, "notifications/initialized", server, new { }, sessionId, effectiveCt).ConfigureAwait(false);
                _ = await OptiRouter.Clients.BoundedResponseReader.ReadBodyAsync(notif.Content, effectiveCt).ConfigureAwait(false);
            }

            // 3. tools/call
            using var callResponse = await SendJsonRpcAsync(
                endpoint, "tools/call", server,
                new { name = toolName, arguments = arguments },
                sessionId, effectiveCt).ConfigureAwait(false);

            string body = await OptiRouter.Clients.BoundedResponseReader.ReadBodyAsync(callResponse.Content, effectiveCt).ConfigureAwait(false);
            var (callOk, callError) = ParseRpcResult(body);
            if (!callOk)
            {
                return new McpToolCallResult(false, string.Empty, $"MCP tools/call failed: {callError}");
            }

            var (success, content, isError) = ParseToolCallResult(body);
            if (!success)
            {
                return new McpToolCallResult(false, string.Empty, "MCP tools/call returned no result.");
            }
            return new McpToolCallResult(!isError, content, isError ? "MCP tool returned isError=true." : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new McpToolCallResult(false, string.Empty, $"MCP tool call timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP tool call {Tool} failed against {BaseUrl}", toolName, server.BaseUrl);
            return new McpToolCallResult(false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 发送 JSON-RPC 请求并返回响应消息（含响应头）。
    /// </summary>
    private async Task<HttpResponseMessage> SendJsonRpcAsync(
        string endpoint,
        string method,
        McpServerRegistration server,
        object? parameters,
        string? sessionId,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = JsonRpcVersion,
                id = Interlocked.Increment(ref _requestId),
                method,
                @params = parameters
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        }
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            int statusCode = (int)response.StatusCode;
            string errorBody = await OptiRouter.Clients.BoundedResponseReader.ReadBodyAsync(response.Content, ct).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"MCP server returned HTTP {statusCode}: {errorBody}");
        }
        return response;
    }

    /// <summary>
    /// 校验 JSON-RPC 响应体：存在 error 字段视为失败。
    /// </summary>
    private static (bool Ok, string Error) ParseRpcResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                string message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error";
                return (false, message);
            }
            return (true, string.Empty);
        }
        catch (JsonException)
        {
            return (false, "Invalid JSON-RPC response.");
        }
    }

    /// <summary>
    /// 解析 tools/call 结果：拼接 result.content 中 text 片段，识别 isError。
    /// </summary>
    private static (bool HasResult, string Content, bool IsError) ParseToolCallResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return (false, string.Empty, false);
            }

            bool isError = result.TryGetProperty("isError", out var isErrorEl) && isErrorEl.ValueKind == JsonValueKind.True;

            var sb = new StringBuilder();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("type", out var typeEl) && typeEl.ValueEquals("text")
                        && item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(textEl.GetString());
                    }
                }
            }

            return (true, sb.ToString(), isError);
        }
        catch (JsonException)
        {
            return (false, string.Empty, false);
        }
    }
}
