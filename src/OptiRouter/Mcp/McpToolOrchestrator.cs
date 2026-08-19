using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP 工具调用编排器 (MCP Tool Orchestrator)。
/// 解析模型响应中的 tool_calls，经注册表定位工具与所属 Server 并执行，
/// 将 assistant 消息（含 tool_calls）与 tool 结果消息回填请求后向同一模型重放，
/// 直至响应不再请求工具或达到轮次上限，形成完整的 Agent 工具调用闭环。
/// </summary>
public sealed class McpToolOrchestrator
{
    /// <summary>
    /// 反序列化 OpenAI 兼容 JSON（snake_case 属性名），与模型客户端选项保持一致。
    /// </summary>
    private static readonly JsonSerializerOptions MessageDeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly McpToolRegistry _registry;
    private readonly IMcpToolExecutor _executor;
    private readonly IModelClientProvider _clientProvider;
    private readonly ILogger<McpToolOrchestrator> _logger;
    private readonly OptiRouter.Endpoints.OutcomeRecorder? _recorder;

    public McpToolOrchestrator(
        McpToolRegistry registry,
        IMcpToolExecutor executor,
        IModelClientProvider clientProvider,
        ILogger<McpToolOrchestrator>? logger = null,
        OptiRouter.Endpoints.OutcomeRecorder? recorder = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _logger = logger ?? NullLogger<McpToolOrchestrator>.Instance;
        _recorder = recorder;
    }

    /// <summary>
    /// 解析 OpenAI 兼容响应 JSON 中的 tool_calls 列表。
    /// </summary>
    public static List<McpPendingToolCall> ExtractToolCalls(string responseBody)
    {
        var calls = new List<McpPendingToolCall>();
        if (string.IsNullOrWhiteSpace(responseBody)) return calls;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return calls;

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                    continue;
                if (!msg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var tc in toolCalls.EnumerateArray())
                {
                    string? id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    string? name = null;
                    var arguments = default(JsonElement);

                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        if (fn.TryGetProperty("arguments", out var argsEl))
                        {
                            if (argsEl.ValueKind == JsonValueKind.String)
                            {
                                try
                                {
                                    arguments = JsonDocument.Parse(argsEl.GetString() ?? "{}").RootElement.Clone();
                                }
                                catch (JsonException)
                                {
                                    arguments = default;
                                }
                            }
                            else if (argsEl.ValueKind == JsonValueKind.Object)
                            {
                                arguments = argsEl.Clone();
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(name)) continue;
                    calls.Add(new McpPendingToolCall(id ?? string.Empty, name, arguments));
                }
            }
        }
        catch (JsonException)
        {
            // 响应不可解析则视为无工具调用
        }

        return calls;
    }

    /// <summary>
    /// 提取响应中 <c>choices[0].message</c> 的原始 JSON（用于保真回填 assistant 消息）。
    /// </summary>
    public static string? ExtractAssistantMessageJson(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            if (!choices[0].TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                return null;
            return msg.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 若响应包含 tool_calls：执行全部工具、组装 assistant + tool 消息并向同一候选重放，
    /// 循环直至无新工具调用或达到 <paramref name="maxRounds"/> 轮。返回最终响应。
    /// </summary>
    public async Task<RawChatResponse> ExecuteToolCallsAndReplayAsync(
        ChatRequest request,
        RawChatResponse response,
        ModelEndpointOptions candidate,
        int maxRounds,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (response?.Body is null) return response!;
        maxRounds = Math.Max(1, maxRounds);

        var client = _clientProvider.GetClient(candidate);
        var currentRequest = request;
        var currentResponse = response;
        // 每次请求内有效的 tool_calls 签名集（防模型无进展地重复相同调用）
        var _seenCallSignatures = new HashSet<string>(StringComparer.Ordinal);

        for (int round = 0; round < maxRounds; round++)
        {
            var toolCalls = ExtractToolCalls(currentResponse.Body);
            if (toolCalls.Count == 0) break;

            // 该响应即将被本轮重放结果取代：真实花费立即入账（含入口首轮）。
            // 多轮工具调用的中间请求此前全部漏计，导致含工具请求的日/会话预算系统性低估；
            // 最终返回的响应由调用方（ProxyOrchestrator）计费，此处不重复入账。
            if (_recorder is not null && currentResponse.Usage is not null)
            {
                decimal supersededCost = OptiRouter.Endpoints.CostCalculator.Compute(currentResponse.Usage, candidate);
                if (supersededCost > 0m)
                {
                    _recorder.RecordCost(supersededCost, sessionId);
                }
            }

            // 无进展检测：模型重复请求完全相同的 tool_calls（名称+参数签名）说明工具结果没能推动它前进，
            // 继续重放只会烧满剩余轮次的 token 与延迟——检测到重复签名即终止循环返回当前响应。
            string roundSignature = string.Join("|", toolCalls.Select(c => $"{c.Name}:{c.Arguments.GetRawText()}"));
            if (!_seenCallSignatures.Add(roundSignature))
            {
                _logger.LogWarning("MCP tool loop round {Round}: duplicate tool_calls signature detected, stopping replay loop", round);
                break;
            }

            string? assistantMessageJson = ExtractAssistantMessageJson(currentResponse.Body);
            if (assistantMessageJson is null) break;

            ChatMessage assistantMessage;
            try
            {
                assistantMessage = JsonSerializer.Deserialize<ChatMessage>(assistantMessageJson, MessageDeserializeOptions) ?? ChatMessage.FromText("assistant", string.Empty);
            }
            catch (JsonException)
            {
                break;
            }

            var messages = new List<ChatMessage>(currentRequest.Messages ?? []) { assistantMessage };
            foreach (var call in toolCalls)
            {
                var toolResult = await ExecuteSingleToolAsync(call, ct).ConfigureAwait(false);
                // 失败且无内容时，把错误原因回填给模型，使其能感知并自我纠正。
                string toolContent = toolResult.Content;
                if (string.IsNullOrEmpty(toolContent) && !string.IsNullOrEmpty(toolResult.ErrorMessage))
                {
                    toolContent = $"Tool execution failed: {toolResult.ErrorMessage}";
                }
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = JsonSerializer.SerializeToElement(toolContent),
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["tool_call_id"] = JsonSerializer.SerializeToElement(call.Id)
                    }
                });
                _registry.RecordToolExecution(call.Name, toolResult.IsSuccess, 0);
            }

            var replayRequest = currentRequest with { Messages = messages };
            _logger.LogInformation("MCP tool round {Round}: executed {Count} tool(s), replaying to {Model}",
                round + 1, toolCalls.Count, candidate.Name);
            currentResponse = await client.CompleteRawAsync(replayRequest, ct).ConfigureAwait(false);
            currentRequest = replayRequest;
        }

        return currentResponse;
    }

    private async Task<McpToolCallResult> ExecuteSingleToolAsync(McpPendingToolCall call, CancellationToken ct)
    {
        var registration = _registry.GetTool(call.Name);
        if (registration is null)
        {
            return new McpToolCallResult(false, string.Empty, $"Tool '{call.Name}' is not registered in the MCP tool registry.");
        }

        var server = _registry.GetServer(registration.ServerName);
        if (server is null)
        {
            return new McpToolCallResult(false, string.Empty, $"Tool '{call.Name}' references unknown MCP server '{registration.ServerName}'.");
        }

        if (!server.Enabled)
        {
            return new McpToolCallResult(false, string.Empty, $"MCP server '{server.Name}' is disabled.");
        }

        return await _executor.ExecuteToolAsync(server, call.Name, call.Arguments, ct).ConfigureAwait(false);
    }
}
