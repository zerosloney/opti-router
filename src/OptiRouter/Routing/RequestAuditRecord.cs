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
/// <param name="RoutedTier">路由命中档（首选候选的 tier）。用于离线评估路由分档正确性。</param>
/// <param name="CascadeTriggered">是否触发了 Cheap→Strong 级联自校验。</param>
/// <param name="UpgradedFrom">升级源模型名（null = 无升级）。配合 <paramref name="CascadeTriggered"/> 追踪升级链。</param>
/// <param name="IsAdopted">并行首试模式下，本次尝试是否被采纳（响应实际返回给客户端）。
/// 串行模式/级联重答恒为 true（默认值）。并行模式下同组共享 <paramref name="ParallelGroupId"/>，仅采纳者 IsAdopted=true。
/// 用于区分"被取消的慢尝试"与"实际生效的响应"。</param>
/// <param name="ParallelGroupId">并行首试组 ID。串行模式为 null。并行模式下同一次 SendAsync 的多个并行尝试共享此 ID。</param>
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
    bool IsStreaming,
    ModelTier RoutedTier = ModelTier.Medium,
    bool CascadeTriggered = false,
    string? UpgradedFrom = null,
    bool IsAdopted = true,
    string? ParallelGroupId = null);
