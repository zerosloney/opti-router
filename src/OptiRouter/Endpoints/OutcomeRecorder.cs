using Microsoft.AspNetCore.Http;
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
    private readonly ContextualBanditState? _banditStore;
    private readonly PromptCacheAffinityStore _promptAffinityStore;
    private readonly UpstreamQuotaStateStore _quotaStore;
    private readonly KalmanLatencyTracker? _kalmanTracker;
    private readonly KvCachePrefixTrie? _kvCacheTrie;
    private readonly PredictiveResilienceEngine? _resilienceEngine;
    private readonly Mesh.DistributedMeshSynchronizer? _meshSynchronizer;
    private readonly ILogger<OutcomeRecorder> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ClientKeyService? _clientKeyService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly CalibratingTokenEstimator? _calibratingEstimator;
    private readonly SessionLatencyTracker? _sessionLatencyTracker;

    public OutcomeRecorder(
        IRequestAuditStore auditStore,
        RouterMetrics metrics,
        CostLedger ledger,
        IOptionsMonitor<RouterOptions> options,
        IMemoryCache affinityCache,
        ThompsonStateStore tsStore,
        PromptCacheAffinityStore promptAffinityStore,
        UpstreamQuotaStateStore quotaStore,
        ILogger<OutcomeRecorder> logger,
        TimeProvider? timeProvider = null,
        ContextualBanditState? banditStore = null,
        ClientKeyService? clientKeyService = null,
        IHttpContextAccessor? httpContextAccessor = null,
        CalibratingTokenEstimator? calibratingEstimator = null,
        KalmanLatencyTracker? kalmanTracker = null,
        KvCachePrefixTrie? kvCacheTrie = null,
        PredictiveResilienceEngine? resilienceEngine = null,
        Mesh.DistributedMeshSynchronizer? meshSynchronizer = null,
        SessionLatencyTracker? sessionLatencyTracker = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _banditStore = banditStore;
        _clientKeyService = clientKeyService;
        _httpContextAccessor = httpContextAccessor;
        _calibratingEstimator = calibratingEstimator;
        _kalmanTracker = kalmanTracker;
        _kvCacheTrie = kvCacheTrie;
        _resilienceEngine = resilienceEngine;
        _meshSynchronizer = meshSynchronizer;
        _sessionLatencyTracker = sessionLatencyTracker;
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
        bool quotaLimited = false,
        double? reward = null,
        string? epsilonPromotedModel = null,
        string? requestContent = null,
        string? classificationSignal = null)
    {
        try
        {
            // requestId 未显式传入时回退当前 HTTP 上下文（入口中间件已把 X-Request-Id 或
            // 生成的 GUID 放入 Items["RequestId"]）——与下方 TraceScope 的 ambient 语义对齐，
            // 一次性覆盖 ProxyOrchestrator/FusionRouter/RaceOrchestrator 等全部调用点。
            requestId ??= _httpContextAccessor?.HttpContext?.Items["RequestId"] as string;

            // 估算校准：成功请求用上游精确 usage 回填 EMA 比率，修正分桶估算的系统性偏低。
            if (usage is { PromptTokens: >= 200 } && estimatedTokens > 0)
                _calibratingEstimator?.Observe(estimatedTokens, usage.PromptTokens);

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
                QuotaLimited: quotaLimited,
                TraceId: TraceScope.Current?.TraceId,
                SpanId: TraceScope.Current?.SpanId,
                ParentSpanId: TraceScope.Current?.ParentSpanId,
                Reward: reward,
                EpsilonPromotedModel: epsilonPromotedModel,
                RequestContent: requestContent,
                ClassificationSignal: classificationSignal));
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

        try
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                _resilienceEngine?.RecordObservation(model, success, latencyMs);
                var routingOpt = _options.CurrentValue.Routing;

                if (routingOpt.EnableDistributedStateMesh && routingOpt.MeshBroadcastResilience && _meshSynchronizer != null)
                {
                    _ = _meshSynchronizer.BroadcastResilienceOutcomeAsync(model, !success);
                }

                if (success && latencyMs > 0)
                {
                    _kalmanTracker?.RecordObservation(model, latencyMs);
                    if (routingOpt.EnableDistributedStateMesh && routingOpt.MeshBroadcastKalman && _meshSynchronizer != null)
                    {
                        _ = _meshSynchronizer.BroadcastKalmanLatencyAsync(model, latencyMs);
                    }

                    if (!string.IsNullOrWhiteSpace(requestContent))
                    {
                        var req = new OptiRouter.Clients.ChatRequest
                        {
                            Messages = new List<OptiRouter.Clients.ChatMessage> { OptiRouter.Clients.ChatMessage.FromText("user", requestContent) }
                        };
                        _kvCacheTrie?.RecordCachePrefix(req, model);

                        if (routingOpt.EnableDistributedStateMesh && routingOpt.MeshBroadcastKvCache && _meshSynchronizer != null)
                        {
                            var tokens = KvCachePrefixTrie.ExtractPrefixTokens(req);
                            if (tokens.Count >= 3)
                            {
                                _ = _meshSynchronizer.BroadcastKvCachePrefixAsync(tokens, model);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 卡尔曼滤波、前缀、网格同步与时序弹性记录失败不得影响请求路径。
        }
    }

    /// <summary>
    /// 预算预留透传：请求发起前预扣 in-flight 预估成本，防止并发请求在计费落账前
    /// 集体越过预算线（TOCTOU）。同时覆盖全局账本（<see cref="CostLedger.Reserve"/>）
    /// 与授权租户的日预算账户（<see cref="ClientKeyService.ReserveSpend"/>）。
    /// </summary>
    public void ReserveCostEstimate(decimal amount, string? sessionId)
    {
        _ledger.Reserve(amount, sessionId);
        try
        {
            string? keyId = AuthorizedTenantKeyId();
            if (keyId is not null)
                _clientKeyService?.ReserveSpend(keyId, amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant spend reservation failed; in-flight spend may under-block");
        }
    }

    /// <summary>
    /// 释放预算预留，严格与 <see cref="ReserveCostEstimate"/> 配对（请求 finally）。
    /// </summary>
    public void ReleaseCostEstimate(decimal amount, string? sessionId)
    {
        _ledger.Release(amount, sessionId);
        try
        {
            string? keyId = AuthorizedTenantKeyId();
            if (keyId is not null)
                _clientKeyService?.ReleaseSpend(keyId, amount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant spend reservation release failed; reservation may leak until process restart");
        }
    }

    /// <summary>
    /// 当前请求授权租户的 KeyId；无租户上下文（全局代理 Key/管理端）返回 null。
    /// </summary>
    private string? AuthorizedTenantKeyId()
    {
        if (_clientKeyService is null
            || _httpContextAccessor?.HttpContext?.Items[typeof(ClientKeyAuthorizationResult)]
                is not ClientKeyAuthorizationResult { Status: ClientKeyAuthorizationStatus.Authorized } authorization
            || string.IsNullOrWhiteSpace(authorization.KeyId))
        {
            return null;
        }
        return authorization.KeyId;
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

            var routerOpt = _options.CurrentValue;
            var routingOpt = routerOpt.Routing;
            bool hasSharedCostLedger = string.Equals(routerOpt.Budget.StoreProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
                || string.Equals(routerOpt.Budget.StoreProvider, "Redis", StringComparison.OrdinalIgnoreCase);
            if (!hasSharedCostLedger
                && routingOpt.EnableDistributedStateMesh
                && routingOpt.MeshBroadcastCostLedger
                && _meshSynchronizer != null)
            {
                _ = _meshSynchronizer.BroadcastCostAsync(cost, sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cost ledger write failed; cost {Cost} not recorded", cost);
        }

        try
        {
            string? keyId = AuthorizedTenantKeyId();
            if (keyId is null)
            {
                return;
            }

            _clientKeyService!.RecordSpend(keyId, cost);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant cost persistence failed; cost {Cost} not recorded", cost);
        }
    }

    /// <summary>
    /// 记录会话粘性：成功命中某模型后写入内存缓存，供 <see cref="SessionAffinityPolicy"/> 下次决策提升该模型。
    /// 仅在启用会话粘性且存在 sessionId 时写。写失败（理论上 IMemoryCache 不会抛）不影响主流程。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="modelName">成功命中的模型名。</param>
    /// <param name="signal">
    /// 信号强度。主链成功传 <see cref="AffinitySignal.Strong"/>（总是覆盖）；
    /// Cascade/Fusion/Race 等旁路成功传 <see cref="AffinitySignal.Weak"/>——
    /// 仅当无现有粘性或现有粘性已超过一个 TTL 周期（视为不新鲜）时才接管，避免旁路的
    /// 偶发/升级路径覆盖主链刚建立的稳定偏好。
    /// </param>
    /// <param name="latencyMs">
    /// 本次请求延迟（毫秒，>0）。同时写入 <see cref="SessionLatencyTracker"/>，供 SessionAffinityPolicy
    /// "延迟熔断"逃生通道使用。&lt;=0 或 tracker 未注入时静默忽略。默认 0（向后兼容）。
    /// </param>
    public void RecordAffinity(string? sessionId, string modelName, AffinitySignal signal = AffinitySignal.Strong, long latencyMs = 0)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        var routing = _options.CurrentValue.Routing;
        if (!routing.EnableSessionAffinity)
            return;

        int ttl = routing.SessionAffinityTtlSeconds > 0 ? routing.SessionAffinityTtlSeconds : 600;
        string key = SessionAffinityPolicy.CacheKeyPrefix + sessionId;
        var now = _timeProvider.GetUtcNow();

        try
        {
            // 弱信号：若已存在新鲜的主链偏好则保留，不覆盖。
            bool keepExistingAffinity = signal == AffinitySignal.Weak
                && _affinityCache.TryGetValue<AffinityRecord>(key, out var existing)
                && existing is not null
                && now - existing.UpdatedAt < TimeSpan.FromSeconds(ttl);
            if (!keepExistingAffinity)
            {
                _affinityCache.Set(key, new AffinityRecord(modelName, now), new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl),
                    Size = 1
                });
            }
        }
        catch
        {
            // 粘性记录失败不应影响已成功的请求。
        }

        // 延迟熔断用数据：与粘性记录一起写入 tracker。tracker 未注入时静默忽略（不破坏旧行为）。
        // 只在"成功"路径上写；失败请求的延迟不计入（避免污染分布）。
        try
        {
            if (latencyMs > 0)
                _sessionLatencyTracker?.Record(sessionId, latencyMs, routing.SessionAffinityEscapeWindowSize,
                    TimeSpan.FromSeconds(ttl));
        }
        catch
        {
            // 延迟窗口写入失败不应影响主流程。
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
    /// 若启用上下文 bandit（<see cref="RoutingOptions.EnableContextualBandit"/>），同步更新 LinUCB 状态。
    /// 返回应用后的 reward（成本加权后）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="elapsedMs">
    /// 本次请求端到端延迟（毫秒）。<c>null</c> 表示硬失败（网络/超时/上游错误），reward 0.0；
    /// 否则经 <see cref="MapLatencyToReward"/> 平滑映射（越快越高，无阶跃），目标由 <paramref name="actualTier"/> 解析。
    /// </param>
    /// <param name="classificationSignal">分类信号（供上下文 bandit 特征构造）；null = 不更新 bandit。</param>
    /// <param name="classificationTargetTier">目标 tier（供上下文 bandit 特征构造）。</param>
    /// <param name="cost">本次请求成本（USD），>0 时参与成本感知复合 reward。</param>
    /// <param name="actualTier">实际路由到的模型 tier，用于 per-tier 延迟目标解析；null（默认）回退全局目标。</param>
    /// <param name="qualityFactor">质量因子 ∈ [0,1]（默认 1.0=不折减）。由 <see cref="ExtractQualityFactor"/> 从响应计算，
    /// 低质量信号时乘性折减延迟 reward。仅此路径生效；显式质量入口与竞速取消不乘。</param>
    public double RecordThompsonOutcome(string modelName, long? elapsedMs,
        string? classificationSignal = null, ModelTier? classificationTargetTier = null, decimal cost = 0,
        ModelTier? actualTier = null, double qualityFactor = 1.0)
    {
        var routing = _options.CurrentValue.Routing;
        double latencyReward = MapLatencyToReward(elapsedMs, ResolveLatencyTarget(actualTier, routing));
        double reward = latencyReward * Math.Clamp(qualityFactor, 0.0, 1.0);
        return ApplyOutcome(modelName, reward, cost, classificationSignal, classificationTargetTier);
    }

    /// <summary>使用完整路由决策记录反馈，确保决策与学习使用相同的请求上下文。返回应用后的 reward。</summary>
    public double RecordThompsonOutcome(string modelName, long? elapsedMs, RouterDecision decision, decimal cost = 0,
        ModelTier? actualTier = null, double qualityFactor = 1.0, int completionTokens = 0, long? timeToFirstTokenMs = null)
    {
        var routing = _options.CurrentValue.Routing;
        long? effectiveLatencyMs = ComputeEffectiveLatencyMs(elapsedMs, decision, routing, completionTokens, timeToFirstTokenMs);
        double latencyReward = MapLatencyToReward(effectiveLatencyMs, ResolveLatencyTarget(actualTier, routing));
        double reward = latencyReward * Math.Clamp(qualityFactor, 0.0, 1.0);
        return ApplyOutcome(modelName, reward, cost, decision);
    }

    /// <summary>
    /// 上报竞速失败反馈：模型在并行竞速中被更快者比下去而取消，非自身故障。
    /// 计部分正奖励（<see cref="RoutingOptions.ThompsonRaceCancelledReward"/>，默认 0.5），
    /// 不完全惩罚——模型可能只是慢/运气差，未必坏。值可运行时配置，按观测效果调参。
    /// 若启用上下文 bandit，同步更新 LinUCB 状态。
    /// 返回应用后的 reward（成本加权后）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="classificationSignal">分类信号（供上下文 bandit 特征构造）；null = 不更新 bandit。</param>
    /// <param name="classificationTargetTier">目标 tier（供上下文 bandit 特征构造）。</param>
    /// <param name="cost">本次请求成本（USD），>0 时参与成本感知复合 reward。</param>
    public double RecordThompsonRaceCancelled(string modelName,
        string? classificationSignal = null, ModelTier? classificationTargetTier = null, decimal cost = 0)
    {
        double reward = _options.CurrentValue.Routing.ThompsonRaceCancelledReward;
        return ApplyOutcome(modelName, reward, cost, classificationSignal, classificationTargetTier);
    }

    /// <summary>使用完整路由决策记录竞速取消反馈。返回应用后的 reward。</summary>
    public double RecordThompsonRaceCancelled(string modelName, RouterDecision decision, decimal cost = 0)
        => ApplyOutcome(modelName, _options.CurrentValue.Routing.ThompsonRaceCancelledReward, cost, decision);

    /// <summary>
    /// 上报显式质量驱动的 reward（绕过延迟映射）。用于已有质量判定信号（如级联自校验置信度）接入学习状态。
    /// 此前质量信号被丢弃，Thompson/Bandit 只看延迟+硬失败，系统性偏好"快但不一定准"的模型——
    /// 此方法把质量事件显式注入，使学习状态能感知"答得对不对"，而非仅"快不快/崩没崩"。
    /// reward 被 Clamp 到 [0,1]：1.0=质量高（强化），0.0=质量差（惩罚）。衰减与 bandit 更新同主路径。
    /// 返回应用后的 reward（成本加权后）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="qualityReward">质量 reward，将在内部 Clamp 到 [0,1]。</param>
    /// <param name="classificationSignal">分类信号（供上下文 bandit 特征构造）；null = 不更新 bandit。</param>
    /// <param name="classificationTargetTier">目标 tier（供上下文 bandit 特征构造）。</param>
    /// <param name="cost">本次请求成本（USD），>0 时参与成本感知复合 reward。</param>
    public double RecordQualityOutcome(string modelName, double qualityReward,
        string? classificationSignal = null, ModelTier? classificationTargetTier = null, decimal cost = 0)
    {
        double reward = Math.Clamp(qualityReward, 0.0, 1.0);
        return ApplyOutcome(modelName, reward, cost, classificationSignal, classificationTargetTier);
    }

    /// <summary>使用完整路由决策记录质量反馈。返回应用后的 reward。</summary>
    public double RecordQualityOutcome(string modelName, double qualityReward, RouterDecision decision, decimal cost = 0)
        => ApplyOutcome(modelName, Math.Clamp(qualityReward, 0.0, 1.0), cost, decision);

    /// <summary>
    /// 把 reward 应用到 Thompson 采样状态与（若启用）上下文 bandit。三个上报入口共享此核心，
    /// 区别仅在 reward 来源：延迟映射、竞速取消或显式质量。
    /// 返回应用后的 reward（成本加权后）。
    /// </summary>
    private double ApplyOutcome(string modelName, double reward, decimal cost, string? classificationSignal, ModelTier? classificationTargetTier, int tokens = 0)
    {
        var routing = _options.CurrentValue.Routing;
        reward = ApplyCostWeight(reward, cost, tokens, routing);
        _tsStore.RecordOutcome(modelName, reward, routing.ThompsonDiscountFactor);

        if (routing.EnableContextualBandit && _banditStore is not null && classificationSignal is not null)
        {
            var feature = ContextualBanditFeatureBuilder.Build(classificationSignal, classificationTargetTier);
            _banditStore.Update(modelName, feature, reward, routing.ContextualBanditDiscountFactor);
        }
        return reward;
    }

    private double ApplyOutcome(string modelName, double reward, decimal cost, RouterDecision decision)
    {
        var routing = _options.CurrentValue.Routing;
        // 决策携带 token 估算：成本归一化用它消除"长输入=贵模型"的偏差。
        reward = ApplyCostWeight(reward, cost, decision.EstimatedInputTokens, routing);
        _tsStore.RecordOutcome(modelName, reward, routing.ThompsonDiscountFactor);

        if (routing.EnableContextualBandit && _banditStore is not null)
        {
            _banditStore.Update(
                modelName,
                ContextualBanditFeatureBuilder.Build(decision),
                reward,
                routing.ContextualBanditDiscountFactor);
        }
        return reward;
    }

    /// <summary>
    /// 成本感知复合 reward：(1-α)·原reward + α·costReward。
    /// costReward = baseline/(baseline+pricePerMillion) ∈ (0,1]，pricePerMillion = cost×1e6/tokens
    /// （按 token 归一化的等效 $/M 价格）。绝对花费随输入长度线性增长，不归一化会把长上下文请求
    /// 的所有模型都误判为贵；归一化后引导 Bandit/Thompson 在质量/延迟相近时偏好真正便宜的模型。
    /// α = <see cref="RoutingOptions.CostAwareWeight"/>（默认 0=禁用，保持原 reward）；
    /// cost=0（未知/免费）或 tokens=0（无法归一化，回退绝对花费口径）时不做 token 归一化。
    /// </summary>
    private static double ApplyCostWeight(double reward, decimal cost, int tokens, RoutingOptions routing)
    {
        if (routing.CostAwareWeight <= 0 || cost <= 0)
            return reward;
        double alpha = routing.CostAwareWeight;
        double baseline = (double)routing.CostAwareBaselineUsd;
        double normalizedCost = tokens > 0
            ? (double)cost * 1_000_000.0 / tokens
            : (double)cost;
        double costReward = baseline / (baseline + normalizedCost);
        return (1.0 - alpha) * reward + alpha * costReward;
    }

    /// <summary>
    /// 按优先级计算有效延迟：
    /// 1. elapsedMs 为 null → null（硬失败）
    /// 2. 流式请求且有 TTFT → 用 TTFT（交互体验由首 token 延迟主导，不做输出归一化）
    /// 3. 输出归一化启用且 completionTokens 超过基准 → 折算延迟（elapsedMs × refTokens / completionTokens）
    /// 4. 否则 → 原延迟值
    /// </summary>
    private static long? ComputeEffectiveLatencyMs(
        long? elapsedMs,
        RouterDecision decision,
        RoutingOptions routing,
        int completionTokens,
        long? timeToFirstTokenMs)
    {
        // a. 硬失败
        if (!elapsedMs.HasValue)
            return null;

        // b. TTFT 优先：流式下输出未完成，不做归一化
        if (decision.RequestIsStreaming && timeToFirstTokenMs is > 0)
            return timeToFirstTokenMs.Value;

        // c. 输出归一化：仅当输出超过基准时折算
        int refTokens = routing.ThompsonLatencyNormalizeRefTokens;
        if (refTokens > 0 && completionTokens > refTokens)
        {
            // 用 double 计算避免整数溢出，结果恒 ≤ elapsedMs
            double normalized = elapsedMs.Value * refTokens / (double)completionTokens;
            return (long)normalized;
        }

        // d. 默认：不折算
        return elapsedMs;
    }

    /// <summary>
    /// 把端到端延迟平滑映射为 reward ∈ [0,1]。单调分段线性（越快越高，无阶跃）：
    /// <c>null</c>（失败）→ 0.0；<c>[0, target]</c> 线性 1.0→0.7；<c>(target, 2·target]</c> 线性 0.7→0.3；
    /// <c>&gt; 2·target</c> → 0.3（慢成功地板，保留正信号，避免极端 outlier 等同失败）。
    /// 替代原 0/0.3/1.0 三档阶跃，消除"压线突变"与学习曲线粗糙。
    /// </summary>
    public static double MapLatencyToReward(long? elapsedMs, double targetMs)
    {
        if (!elapsedMs.HasValue) return 0.0;
        double ms = elapsedMs.Value;
        if (ms <= 0) return 1.0;
        if (targetMs <= 0) targetMs = 1.0; // 防御除零；合法 target 由 validator 保证 >0
        if (ms <= targetMs)
            return 1.0 - 0.3 * (ms / targetMs);                  // 1.0 → 0.7
        if (ms <= 2.0 * targetMs)
            return 0.7 - 0.4 * ((ms - targetMs) / targetMs);     // 0.7 → 0.3
        return 0.3;                                              // 慢成功地板
    }

    /// <summary>
    /// 解析某 tier 的延迟目标：命中 <see cref="RoutingOptions.ThompsonLatencyTargetMsByTier"/> 且 &gt;0 用之，
    /// 否则回退全局 <see cref="RoutingOptions.ThompsonLatencyTargetMs"/>。
    /// 消除"全局单 target 系统性偏 Cheap"——强模型天生慢，用更宽松目标避免被系统性惩罚。
    /// </summary>
    public static double ResolveLatencyTarget(ModelTier? actualTier, RoutingOptions routing)
    {
        if (actualTier.HasValue
            && routing.ThompsonLatencyTargetMsByTier.TryGetValue(actualTier.Value, out double tierTarget)
            && tierTarget > 0)
        {
            return tierTarget;
        }
        return routing.ThompsonLatencyTargetMs;
    }

    /// <summary>
    /// 从非流式响应提取质量因子 ∈ [0,1]，用于乘性折减延迟 reward。
    /// <c>null</c> 响应（无可判内容）→ 1.0（不惩罚，失败由 latency reward=0 处理）；
    /// 低质量信号（finish_reason=length 截断 / content_filter / 空 content / JSON 契约违约）→ <paramref name="penalty"/>；
    /// 否则 → 1.0。只判空 content，不引入"极短"主观阈值（避免误伤 "yes"/"42" 等正常短答）。
    /// JSON 契约违约：请求显式要求 JSON 输出（response_format=json_object/json_schema）但 content
    /// 不是合法 JSON——客观可验证的质量信号，无需采样/额外调用，每次请求都可用。
    /// </summary>
    /// <param name="response">上游原始响应。</param>
    /// <param name="penalty">低质量惩罚因子。</param>
    /// <param name="request">原始请求（用于检测 JSON 契约）；null = 跳过契约校验。</param>
    public static double ExtractQualityFactor(RawChatResponse? response, double penalty, ChatRequest? request = null)
    {
        if (response is null) return 1.0;
        var (content, finishReason) = ResponseConfidenceChecker.ExtractAssistantContentAndFinishReason(response);
        if (IsLowQualitySignal(content, finishReason))
            return Math.Clamp(penalty, 0.0, 1.0);
        if (RequestExpectsJson(request) && !IsValidJson(content))
            return Math.Clamp(penalty, 0.0, 1.0);
        return 1.0;
    }

    private static bool IsLowQualitySignal(string content, string finishReason)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        if (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase)) return true;
        if (finishReason.Equals("content_filter", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 检测请求是否显式要求 JSON 输出：ExtensionData 里的 response_format 为
    /// "json_object"/"json_schema" 字符串，或 { "type": "json_object"/"json_schema" } 对象。
    /// </summary>
    private static bool RequestExpectsJson(ChatRequest? request)
    {
        if (request?.ExtensionData is not { Count: > 0 } ext)
            return false;
        if (!ext.TryGetValue("response_format", out var value))
            return false;

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String
                => IsJsonFormatType(value.GetString()),
            System.Text.Json.JsonValueKind.Object
                => value.TryGetProperty("type", out var type)
                   && type.ValueKind == System.Text.Json.JsonValueKind.String
                   && IsJsonFormatType(type.GetString()),
            _ => false
        };
    }

    private static bool IsJsonFormatType(string? formatType)
        => formatType is "json_object" or "json_schema";

    private static bool IsValidJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            _ = System.Text.Json.JsonDocument.Parse(content);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            // 常见包装：模型在 JSON 外围加了 ```json 围栏或前后说明文字。
            // 只剥一层围栏再试一次，仍失败判为契约违约。
            return TryParseStrippedOfFences(content);
        }
    }

    private static bool TryParseStrippedOfFences(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            int closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && closing > firstNewline)
            {
                string inner = trimmed.Substring(firstNewline + 1, closing - firstNewline - 1).Trim();
                if (inner.Length > 0)
                {
                    try
                    {
                        _ = System.Text.Json.JsonDocument.Parse(inner);
                        return true;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return false;
                    }
                }
            }
        }
        return false;
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
