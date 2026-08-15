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
/// <param name="IsEstimated">本次成本是否为预估值（非上游真实 Usage）。
/// 并行首试中被取消/失败的尝试拿不到上游 Usage，按 EstimatedInputTokens × 模型 input 价格预估入账，
/// 标注此字段以区分真实成本。串行模式/采纳的成功响应恒为 false（默认值）。</param>
/// <param name="FusionRole">融合路由中的角色：<c>panel</c>（并行 panel 调用）、<c>analyst</c>（结构化分析）、
/// <c>outer</c>（最终答案撰写）。非融合路由为 null（默认值）。</param>
/// <param name="TimeToFirstTokenMs">流式首个 data 项 TTFT；非流式为响应头延迟代理。</param>
/// <param name="CachedInputTokens">缓存命中输入 token。</param>
/// <param name="CacheWriteInputTokens">缓存写入输入 token。</param>
/// <param name="UncachedInputTokens">未缓存输入 token。</param>
/// <param name="QuotaLimited">是否为上游配额拒绝。</param>
    /// <param name="TraceId">W3C 规范的分布式 Trace ID。</param>
    /// <param name="SpanId">W3C 规范的子节点 Span ID。</param>
    /// <param name="ParentSpanId">父节点 Span ID（用于构建 DAG 树）。</param>
    /// <param name="Reward">本次尝试写入学习状态（Thompson/Bandit）的最终复合 reward（成本加权后）。null = 未记录（如缓存命中行）。</param>
    /// <param name="EpsilonPromotedModel">本次路由决策中被 ε 探索提升到段首的模型名（决策级信息，同决策的所有尝试行共享同一值）；null = 无探索提升。</param>
    /// <param name="RequestContent">请求内容摘要（截断到 500 字符，用于 dashboard 展示）。</param>
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
    string? ParallelGroupId = null,
    bool IsEstimated = false,
    string? FusionRole = null,
    long? TimeToFirstTokenMs = null,
    int CachedInputTokens = 0,
    int CacheWriteInputTokens = 0,
    int UncachedInputTokens = 0,
    bool QuotaLimited = false,
    string? TraceId = null,
    string? SpanId = null,
    string? ParentSpanId = null,
    double? Reward = null,
    string? EpsilonPromotedModel = null,
    string? RequestContent = null);
