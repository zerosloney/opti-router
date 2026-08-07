using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 单条请求审计记录。
/// </summary>
/// <param name="Timestamp">请求完成 UTC 时间。</param>
    /// <param name="RequestId">HTTP 请求 ID（X-Request-Id），可能为 null。</param>
    /// <param name="Model">实际使用的模型名。</param>
/// <param name="EstimatedInputTokens">路由引擎估算的输入 token 数。</param>
/// <param name="PromptTokens">实际 prompt token 数（上游返回，可能为 0）。</param>
/// <param name="CompletionTokens">实际补全 token 数（上游返回，可能为 0）。</param>
/// <param name="Cost">本次请求成本（USD）。</param>
/// <param name="LatencyMs">端到端延迟（毫秒）。</param>
/// <param name="SessionId">会话 ID（可能为 null）。</param>
/// <param name="RoutingReason">路由决策原因字符串。</param>
/// <param name="Success">是否成功（上游返回有效响应）。</param>
/// <param name="ErrorMessage">失败时错误信息（成功时为 null）。</param>
/// <param name="IsStreaming">是否为流式请求。</param>
public sealed record RequestAuditRecord(
    DateTime Timestamp,
    string? RequestId,
    string Model,
    int EstimatedInputTokens,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    long LatencyMs,
    string? SessionId,
    string RoutingReason,
    bool Success,
    string? ErrorMessage,
    bool IsStreaming);
