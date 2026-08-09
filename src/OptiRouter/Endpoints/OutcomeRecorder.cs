using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Metrics;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 统一的请求结果记录器：把审计、成本账本、Prometheus 指标、会话粘性、Thompson 反馈
/// 五类副作用集中到一处，供 <see cref="ProxyOrchestrator"/> 及其拆分出的子策略复用。
/// 所有写入均吞异常——记录失败不得影响已成功的请求路径。
/// </summary>
public sealed class OutcomeRecorder
{
    private readonly IRequestAuditStore _auditStore;
    private readonly RouterMetrics _metrics;
    private readonly CostLedger _ledger;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly IMemoryCache _affinityCache;
    private readonly ThompsonStateStore _tsStore;
    private readonly PromptCacheAffinityStore _promptAffinityStore;
    private readonly UpstreamQuotaStateStore _quotaStore;
    private readonly ILogger<OutcomeRecorder> _logger;

    public OutcomeRecorder(
        IRequestAuditStore auditStore,
        RouterMetrics metrics,
        CostLedger ledger,
        IOptionsMonitor<RouterOptions> options,
        IMemoryCache affinityCache,
        ThompsonStateStore tsStore,
        PromptCacheAffinityStore promptAffinityStore,
        UpstreamQuotaStateStore quotaStore,
        ILogger<OutcomeRecorder> logger)
    {
        _auditStore = auditStore;
        _metrics = metrics;
        _ledger = ledger;
        _options = options;
        _affinityCache = affinityCache;
        _tsStore = tsStore;
        _promptAffinityStore = promptAffinityStore;
        _quotaStore = quotaStore;
        _logger = logger;
    }

    /// <summary>
    /// 记录一条审计记录并同步 Prometheus 指标。参数与 <see cref="RequestAuditRecord"/> 字段一一对应
    /// （<see cref="RequestAuditRecord.Timestamp"/> 在此方法内取 <see cref="DateTime.UtcNow"/>）。
    /// 审计或指标写入异常被吞掉，不向上抛。
    /// </summary>
    public void RecordAudit(
        string? requestId,
        string model,
        int estimatedTokens,
        ChatUsage? usage,
        decimal cost,
        long latencyMs,
        string? sessionId,
        string routingReason,
        bool success,
        string? errorMessage,
        bool isStreaming,
        ModelTier routedTier,
        bool cascadeTriggered = false,
        string? upgradedFrom = null,
        bool isAdopted = true,
        string? parallelGroupId = null,
        bool isEstimated = false,
        string? fusionRole = null,
        long? timeToFirstTokenMs = null,
        bool quotaLimited = false)
    {
        try
        {
            _auditStore.Append(new RequestAuditRecord(
                Timestamp: DateTime.UtcNow,
                RequestId: requestId,
                Model: model,
                EstimatedInputTokens: estimatedTokens,
                PromptTokens: usage?.PromptTokens ?? 0,
                CompletionTokens: usage?.CompletionTokens ?? 0,
                Cost: cost,
                LatencyMs: latencyMs,
                SessionId: sessionId,
                RoutingReason: routingReason,
                Success: success,
                ErrorMessage: errorMessage,
                IsStreaming: isStreaming,
                RoutedTier: routedTier,
                CascadeTriggered: cascadeTriggered,
                UpgradedFrom: upgradedFrom,
                IsAdopted: isAdopted,
                ParallelGroupId: parallelGroupId,
                IsEstimated: isEstimated,
                FusionRole: fusionRole,
                TimeToFirstTokenMs: timeToFirstTokenMs,
                CachedInputTokens: usage?.CachedInputTokens ?? 0,
                CacheWriteInputTokens: usage?.CacheWriteInputTokens ?? 0,
                UncachedInputTokens: usage?.UncachedInputTokens ?? 0,
                QuotaLimited: quotaLimited));
        }
        catch
        {
            // Audit recording must not break the request path.
        }

        // Prometheus 指标：与审计同源（成功/各类失败都经过此方法），记录聚合数。
        // 预估成本（IsEstimated）也计入，与账本语义一致（上游对已发请求计费）。
        try
        {
            _metrics.RecordAttempt(
                model,
                routedTier,
                success,
                errorMessage,
                isStreaming,
                latencyMs,
                usage?.PromptTokens ?? 0,
                usage?.CompletionTokens ?? 0,
                cost,
                timeToFirstTokenMs,
                usage?.CachedInputTokens ?? 0,
                usage?.CacheWriteInputTokens ?? 0,
                usage?.UncachedInputTokens ?? 0,
                quotaLimited);
        }
        catch
        {
            // 指标记录失败不得影响请求路径。
        }
    }

    /// <summary>
    /// 入账成本，写失败不破坏已成功的请求（与审计一致）。
    /// 上游已对请求计费，故账本写失败仅记录告警，不向上抛。
    /// </summary>
    public void RecordCost(decimal cost, string? sessionId)
    {
        try
        {
            _ledger.Record(cost, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cost ledger write failed; cost {Cost} not recorded", cost);
        }
    }

    /// <summary>
    /// 记录会话粘性：成功命中某模型后写入内存缓存，供 <see cref="SessionAffinityPolicy"/> 下次决策提升该模型。
    /// 仅在启用会话粘性且存在 sessionId 时写。写失败（理论上 IMemoryCache 不会抛）不影响主流程。
    /// </summary>
    public void RecordAffinity(string? sessionId, string modelName)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        var routing = _options.CurrentValue.Routing;
        if (!routing.EnableSessionAffinity)
            return;

        int ttl = routing.SessionAffinityTtlSeconds > 0 ? routing.SessionAffinityTtlSeconds : 600;
        try
        {
            _affinityCache.Set(SessionAffinityPolicy.CacheKeyPrefix + sessionId, modelName, TimeSpan.FromSeconds(ttl));
        }
        catch
        {
            // 粘性记录失败不应影响已成功的请求。
        }
    }

    /// <summary>Records stable-prefix affinity using only the SHA-256 fingerprint.</summary>
    public void RecordPromptCacheAffinity(ChatRequest request, string modelName)
    {
        var routing = _options.CurrentValue.Routing;
        if (!routing.EnablePromptCacheAffinity) return;
        string? fingerprint = StablePromptFingerprint.Compute(request);
        if (fingerprint is null) return;
        try
        {
            _promptAffinityStore.Record(
                fingerprint,
                modelName,
                TimeSpan.FromSeconds(routing.PromptCacheAffinityTtlSeconds));
        }
        catch
        {
            // Affinity is advisory and must not affect a successful response.
        }
    }

    /// <summary>Updates process-local normalized quota state.</summary>
    public void RecordQuota(string modelName, UpstreamResponseMetadata? metadata, bool rateLimited = false)
        => _quotaStore.Record(modelName, metadata, rateLimited);

    /// <summary>
    /// 上报 Thompson 采样反馈，读 <see cref="RoutingOptions.ThompsonDiscountFactor"/> 作衰减。
    /// </summary>
    public void RecordThompsonOutcome(string modelName, bool isGood)
    {
        var routing = _options.CurrentValue.Routing;
        _tsStore.RecordOutcome(modelName, isGood, routing.ThompsonDiscountFactor);
    }

    /// <summary>
    /// 按 EstimatedInputTokens × 模型 input 价格预估成本。仅 input 部分（被取消/失败的请求未生成 output）。
    /// 用于并行首试中被取消/失败的尝试——上游对已接收的请求计费，但本地拿不到真实 Usage。
    /// estimatedTokens ≤ 0 或 input 价格为 0 时返回 0（避免记零成本噪声）。
    /// </summary>
    public static decimal EstimateInputCost(ModelEndpointOptions model, int estimatedTokens)
    {
        if (estimatedTokens <= 0) return 0m;
        return estimatedTokens * model.InputPricePerMillion / 1_000_000m;
    }
}
