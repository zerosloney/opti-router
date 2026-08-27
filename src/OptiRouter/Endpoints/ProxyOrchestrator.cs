using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Metrics;
using OptiRouter.Routing;
using OptiRouter.Concurrency;
using OptiRouter.Compliance;

namespace OptiRouter.Endpoints;

/// <summary>
/// 降级重试编排器：按 RouterEngine 给出的候选链顺序尝试，成功即返回，
/// 全部失败则抛出 <see cref="AllCandidatesFailedException"/>。
/// 跨请求失败记忆：通过 <see cref="ModelHealthTracker"/> 上报成败，连续失败达阈值的模型被熔断冷却。
/// </summary>
public sealed class ProxyOrchestrator : IAsyncDisposable, IDisposable
{
    private readonly IModelClientProvider _clientProvider;
    private readonly RouterEngine _engine;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ModelHealthTracker _healthTracker;
    private readonly ILogger<ProxyOrchestrator> _logger;
    private readonly OutcomeRecorder _recorder;
    private readonly CascadeUpgradeHandler _cascadeHandler;
    private readonly FusionRouter _fusionRouter;
    private readonly RaceOrchestrator _raceOrchestrator;
    private readonly IResponseCache _responseCache;
    private readonly ISemanticResponseCache _semanticCache;
    private readonly IAdaptiveConcurrencyLimiter _adaptiveLimiter;
    private readonly IStreamingComplianceFilter _complianceFilter;
    private readonly RegenerateFeedbackTracker _regenerateTracker;
    private readonly OptiRouter.Compression.IPromptPruner _promptPruner;
    private readonly OptiRouter.Mcp.McpToolOrchestrator? _mcpToolOrchestrator;
    private readonly OptiRouter.Compliance.IContentModerator? _contentModerator;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly LlmQualityJudge? _qualityJudge;
    private bool _disposed;

    /// <summary>
    /// 初始化编排器。
    /// </summary>
    public ProxyOrchestrator(
        IModelClientProvider clientProvider,
        RouterEngine engine,
        IOptionsMonitor<RouterOptions> options,
        ModelHealthTracker healthTracker,
        OutcomeRecorder recorder,
        CascadeUpgradeHandler cascadeHandler,
        FusionRouter fusionRouter,
        RaceOrchestrator raceOrchestrator,
        IResponseCache responseCache,
        RegenerateFeedbackTracker regenerateTracker,
        ILogger<ProxyOrchestrator> logger,
        ISemanticResponseCache? semanticCache = null,
        IAdaptiveConcurrencyLimiter? adaptiveLimiter = null,
        IStreamingComplianceFilter? complianceFilter = null,
        OptiRouter.Compression.IPromptPruner? promptPruner = null,
        OptiRouter.Mcp.McpToolOrchestrator? mcpToolOrchestrator = null,
        OptiRouter.Compliance.IContentModerator? contentModerator = null,
        IHttpContextAccessor? httpContextAccessor = null,
        LlmQualityJudge? qualityJudge = null)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(healthTracker);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(cascadeHandler);
        ArgumentNullException.ThrowIfNull(fusionRouter);
        ArgumentNullException.ThrowIfNull(raceOrchestrator);
        ArgumentNullException.ThrowIfNull(responseCache);
        ArgumentNullException.ThrowIfNull(regenerateTracker);
        ArgumentNullException.ThrowIfNull(logger);

        _clientProvider = clientProvider;
        _engine = engine;
        _options = options;
        _healthTracker = healthTracker;
        _recorder = recorder;
        _cascadeHandler = cascadeHandler;
        _fusionRouter = fusionRouter;
        _raceOrchestrator = raceOrchestrator;
        _responseCache = responseCache;
        _semanticCache = semanticCache ?? new SemanticResponseCache();
        _adaptiveLimiter = adaptiveLimiter ?? new AdaptiveConcurrencyLimiter();
        _complianceFilter = complianceFilter ?? new StreamingSlidingWindowFilter(_options.CurrentValue.Routing);
        _regenerateTracker = regenerateTracker;
        _promptPruner = promptPruner ?? new OptiRouter.Compression.AdaptivePromptPruner();
        _mcpToolOrchestrator = mcpToolOrchestrator;
        _contentModerator = contentModerator;
        _httpContextAccessor = httpContextAccessor;
        _qualityJudge = qualityJudge;
        _logger = logger;
    }

    /// <summary>
    /// 非流式发送请求，按候选链顺序尝试，失败则降级到下一候选。
    /// 返回上游原始响应字符串（透明透传）。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="sessionId">可选会话 ID，用于按会话记账。</param>
    /// <returns>原始响应 + token 用量。</returns>
    /// <exception cref="BudgetExhaustedException">预算耗尽且模式为 Reject。</exception>
    /// <exception cref="AllCandidatesFailedException">所有候选均失败。</exception>
    public async Task<RawChatResponse> SendAsync(ChatRequest request, CancellationToken ct, string? sessionId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        // 会话兜底：无 X-Session-Id 时从对话内容派生（须在 PII/压缩改写之前）。
        sessionId ??= DeriveConversationSession(request);
        sessionId = ScopeSessionId(sessionId);

        var options = _options.CurrentValue;
        var failedInThisRequest = new HashSet<string>(StringComparer.Ordinal);
        var attemptedModels = new List<string>();
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;
        int halfOpenMaxProbes = options.Routing.FailoverHalfOpenMaxProbes;
        int halfOpenRequiredSuccesses = options.Routing.FailoverHalfOpenRequiredSuccesses;
        bool failoverEnabled = options.Routing.EnableFailover;

        using var globalCts = (failoverEnabled && options.Routing.FailoverGlobalTimeoutSeconds > 0)
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (globalCts is not null)
        {
            globalCts.CancelAfter(TimeSpan.FromSeconds(options.Routing.FailoverGlobalTimeoutSeconds));
        }
        var effectiveCt = globalCts?.Token ?? ct;

        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;
        bool fusionRouterAttempted = false;
        bool fusionModeAttempted = false;

        // 响应缓存（仅非流式）：在 PII 脱敏**前**用原始请求算键，避免不同 PII 脱敏后占位符相同串扰。
        // 命中即短路返回（不经路由/上游），不记上游成本，仅记一条 cache-hit 审计。
        // 内容审核开启时完全禁用两类缓存，避免缓存响应绕过当前审核策略。
        string securityPartition = BuildSecurityPartition(sessionId);
        string? cacheKey = options.Routing.EnableResponseCache
            && !request.Stream
            && !options.Routing.EnableContentModeration
            ? BuildPartitionedCacheKey(securityPartition, ResponseCacheKey.Compute(request))
            : null;

        // regenerate 负反馈键：与响应缓存同源（规范化请求 SHA256），必须在 PII 脱敏前基于原始请求计算
        // （脱敏占位符相同的不同请求会串扰键）。
        string? feedbackKey = options.Routing.EnableRegenerateFeedback
            ? BuildPartitionedCacheKey(securityPartition, ResponseCacheKey.Compute(request))
            : null;

        bool regeneratePenaltyApplied = false;

        PiiMap? piiMap = null;
        if (options.Routing.EnablePiiAnonymization)
        {
            var anonymized = PiiAnonymizer.AnonymizeRequest(request);
            request = anonymized.SanitizedRequest;
            piiMap = anonymized.PiiMap;
        }

        // 构造请求内容摘要（用于 dashboard 展示），根据开关决定是否提取
        string? requestContent = options.Routing.AuditStoreRequestContent
            ? ExtractRequestContentSummary(request)
            : null;

        if (options.Routing.EnablePersonaDriftProtection && !string.IsNullOrEmpty(sessionId))
        {
            request = PersonaDriftGuard.ApplyPersonaAnchor(request);
        }

        if (options.Routing.EnablePromptCompression)
        {
            // 模式联动：压缩先于路由决策，decision 还未产生，用静态解析拿模式预设
            // （auto:cost 激进 / auto:intel 保守 / 其余配置原值）。
            var compression = RoutingModePolicy.AdjustCompression(
                options.Routing.PromptCompression, RoutingModePolicy.TryResolveMode(request.Model));
            var compResult = _promptPruner.Compress(request, compression);
            if (compResult.WasCompressed)
            {
                request = compResult.CompressedRequest;
            }
        }

        // 内容审核（默认关闭）：对改写后的最终 user 文本做输入审核，违规按策略拒绝。
        // 置于语义缓存查询之前，保证未审核输入不进入缓存。
        if (options.Routing.EnableContentModeration && _contentModerator is not null && ShouldModerate(options.Routing))
        {
            string? userText = GetLastUserPrompt(request);
            if (!string.IsNullOrWhiteSpace(userText))
            {
                var modResult = await _contentModerator.ModerateTextAsync(userText, OptiRouter.Compliance.ModerationDirection.Input, effectiveCt).ConfigureAwait(false);
                if (modResult.IsViolation)
                {
                    _logger.LogWarning("Input blocked by content moderation: category={Category}, score={Score:F3}", modResult.Category, modResult.Score);
                    _recorder.RecordAudit(null, "moderation", 0, null, 0m, 0, sessionId, $"moderation-input-blocked:{modResult.Category}", false, modResult.Reason, false, ModelTier.Cheap, requestContent: requestContent);
                    if (options.Routing.ModerationInputAction == OptiRouter.Compliance.ModerationAction.Block)
                    {
                        throw new OptiRouter.Compliance.ComplianceViolationException(
                            $"Input blocked by content moderation (category: {modResult.Category}).", modResult.Category);
                    }
                }
            }
        }

        // 精确缓存查询必须位于 PII / Persona / 压缩 / 输入审核之后。
        // cacheKey 仍使用审核前的原始请求计算，避免不同 PII 值因脱敏占位符相同而串扰。
        if (cacheKey is not null && _responseCache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            // 缓存命中也要消费 regenerate 信号：用户对同一请求重发 = 对上次答案不满意。
            // 若此处短路返回而不消费，regenerate 请求会拿到相同缓存答案且上次模型不受惩罚，信号被缓存完全屏蔽。
            if (feedbackKey is not null
                && _regenerateTracker.TryConsumeRegenerate(
                    feedbackKey, TimeSpan.FromSeconds(options.Routing.RegenerateFeedbackWindowSeconds), out string previousModel))
            {
                // 缓存命中路径无 RouterDecision（未路由），只更新 Thompson 状态，bandit 无特征不更新。
                _recorder.RecordQualityOutcome(previousModel, options.Routing.RegeneratePenaltyReward);
                _logger.LogInformation(
                    "Regenerate feedback (cache hit): penalizing previous model {Model} with reward {Reward:0.00}",
                    previousModel, options.Routing.RegeneratePenaltyReward);
            }
            _recorder.RecordAudit(null, "cache", 0, null, 0m, 0, sessionId, "response-cache-hit", true, null, false, ModelTier.Cheap);
            return cached;
        }

        // 深度语义向量响应缓存 (Semantic Cache) 尝试相似度匹配。
        // 必须在 PII 脱敏 / Persona 锚定 / 提示词压缩等所有请求改写**之后**执行：
        // TryGet 与成功路径的 Store 使用同一改写后的 prompt 作键，否则脱敏占位符/压缩文本
        // 与原文不同键必然 miss（启用 PII 脱敏时语义缓存整体失效）。
        // 命中返回的缓存响应是上游基于占位符生成的原始文本（未还原），须用当前请求 piiMap
        // 还原——不同用户相同结构请求命中同一缓存项时，各自还原出自己的 PII，不泄漏明文。
        if (CanUseSemanticCache(options.Routing, request, piiMap) && !request.Stream)
        {
            string? promptText = GetLastUserPrompt(request);
            if (!string.IsNullOrWhiteSpace(promptText))
            {
                string semanticPartition = BuildSemanticPartition(request, securityPartition);
                var (semHit, semCached, semSim, _) = await _semanticCache.TryGetAsync(
                    promptText,
                    options.Routing.SemanticCacheSimilarityThreshold,
                    ct,
                    partitionKey: semanticPartition).ConfigureAwait(false);

                if (semHit && semCached is not null)
                {
                    // 缓存命中也要消费 regenerate 信号（与响应缓存命中路径同构）：
                    // 命中即短路返回，若不消费，重发请求拿到相似缓存答案且上次模型不受惩罚，
                    // 信号被语义缓存完全屏蔽。命中路径无 RouterDecision，只更新 Thompson 状态。
                    if (feedbackKey is not null
                        && _regenerateTracker.TryConsumeRegenerate(
                            feedbackKey, TimeSpan.FromSeconds(options.Routing.RegenerateFeedbackWindowSeconds), out string previousModel))
                    {
                        _recorder.RecordQualityOutcome(previousModel, options.Routing.RegeneratePenaltyReward);
                        _logger.LogInformation(
                            "Regenerate feedback (semantic cache hit): penalizing previous model {Model} with reward {Reward:0.00}",
                            previousModel, options.Routing.RegeneratePenaltyReward);
                    }
                    RawChatResponse semResponse = piiMap is { HasSensitiveData: true }
                        ? new RawChatResponse(piiMap.Restore(semCached.Body), semCached.Usage, semCached.Metadata)
                        : semCached;
                    _recorder.RecordAudit(null, "semantic-cache", 0, null, 0m, 0, sessionId, $"semantic-cache-hit (sim={semSim:F3})", true, null, false, ModelTier.Cheap);
                    _logger.LogInformation("Semantic Response Cache HIT: similarity={Similarity:F3}", semSim);
                    return semResponse;
                }
            }
        }

        // in-flight 预算预留作用域：using var 在迭代器提前 Dispose（客户端断开）时也保证释放。
        using var budgetReservation = new BudgetReservationScope(_recorder, sessionId);

        while (true)
        {
            if (globalCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode ?? 408, lastErrorMessage ?? $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.", $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.");
            }

            var decision = _engine.Decide(request, options, failedInThisRequest, sessionId);
            int estimatedTokens = decision.EstimatedInputTokens;
            // 本轮路由命中档（首选候选 tier），用于审计追踪路由分档正确性。
            ModelTier routedTier = decision.Candidates.Count > 0 ? decision.Candidates[0].Tier : ModelTier.Medium;

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Route decision: {Reason}, candidates=[{Names}]",
                    decision.Reason, string.Join(", ", decision.Candidates.Select(c => c.Name)));

            if (decision.Candidates.Count == 0)
            {
                if (decision.BudgetExhausted)
                {
                    // 预算耗尽拒绝也要留审计痕：请求未到上游、不走正常落账路径，
                    // 不记则租户被拒事件在 Requests/Dashboard 完全不可见。
                    _recorder.RecordAudit(null, "budget-guard", decision.EstimatedInputTokens, null, 0m, 0, sessionId,
                        decision.Reason, false, "budget exhausted", false, routedTier, classificationSignal: decision.ClassificationSignal);
                    throw new BudgetExhaustedException(decision.Reason);
                }
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, decision.Reason);
            }

            // 预算预扣：本轮决策已过守卫，立即占用本请求预估成本，使后续并发请求的守卫
            // 看到"已入账 + 全部 in-flight"。Dispose（含迭代器提前终止）释放预留，真实计费后净额即实际。
            // 门控只看旋钮不看 EnableBudgetGuard：租户级预算（ClientKeyService）独立于全局守卫执行，
            // 预留经 OutcomeRecorder 同时覆盖全局账本与授权租户两个维度。
            if (options.Budget.ReservationMaxOutputTokens > 0)
            {
                budgetReservation.Arm(EstimateReservationCost(
                    decision.Candidates[0], decision.EstimatedInputTokens, request, options.Budget.ReservationMaxOutputTokens));
            }

            // regenerate 负反馈：同键请求在窗口内重发且上次为成功 → 惩罚上次模型（每请求只消费一次）。
            // 用当前 decision 构造学习特征——同键意味着请求相同，特征与上次决策一致。
            if (!regeneratePenaltyApplied)
            {
                regeneratePenaltyApplied = true;
                if (feedbackKey is not null
                    && _regenerateTracker.TryConsumeRegenerate(
                        feedbackKey, TimeSpan.FromSeconds(options.Routing.RegenerateFeedbackWindowSeconds), out string previousModel))
                {
                    _recorder.RecordQualityOutcome(previousModel, options.Routing.RegeneratePenaltyReward, decision);
                    _logger.LogInformation(
                        "Regenerate feedback: penalizing previous model {Model} with reward {Reward:0.00}",
                        previousModel, options.Routing.RegeneratePenaltyReward);
                }
            }

            // 融合路由（quality router）优先于 Fusion-lite。单次请求最多尝试一次，失败后可继续
            // 走 Fusion-lite 或串行降级，避免 analyst 失败时重复执行同一套 panel。
            // 复杂度门控（P3）：低于 FusionRouterMinComplexity 的请求（默认 Simple）不触发融合，省 ×N 成本。
            if (failoverEnabled && options.Routing.EnableFusionRouter && !fusionRouterAttempted
                && failedInThisRequest.Count == 0 && !request.Stream
                && decision.RequestComplexity >= options.Routing.FusionRouterMinComplexity
                && decision.Candidates.Count >= 2)
            {
                fusionRouterAttempted = true;
                var fusionResult = await _fusionRouter.ExecuteAsync(
                    request, options, decision, estimatedTokens, routedTier,
                    sessionId, failedInThisRequest, attemptedModels, effectiveCt).ConfigureAwait(false);

                lastModelName = fusionResult.LastModelName;
                lastStatusCode = fusionResult.LastStatusCode;
                lastErrorMessage = fusionResult.LastErrorMessage;

                if (fusionResult.Response is not null)
                {
                    // 获胜路径同样要记录 regenerate 反馈键（串行降级路径在成功处 Record），
                    // 否则同键重发时负反馈找不到实际产出答案的模型。成功时 LastModelName 必非空。
                    if (fusionResult.LastModelName is not null)
                    {
                        _regenerateTracker.Record(feedbackKey, fusionResult.LastModelName, success: true);
                    }
                    return ProcessResponse(fusionResult.Response, piiMap);
                }
                // 失败后继续到 Fusion-lite（若同开且仍有足够候选）或串行降级。
            }

            // 并行首试（Fusion-lite）：首轮 + 非流式 + 启用 + ≥2 候选时，并行尝试前 N 个，取最快成功。
            // 真实失败/取消进 failedInThisRequest，continue 后由串行降级链兜底。
            // 但 Race 在 admitted<2（候选存在但多数熔断、凑不齐并行数）时回退串行，不写入 failedInThisRequest——
            // 此时 failedInThisRequest 仍为空，若无一次性守卫会无限重入本块：默认无全局超时（FailoverGlobalTimeoutSeconds=0），
            // 且 admitted<2 同步返回不观察 ct，客户端断开也无法打破，致线程满 CPU 自旋。故每请求最多触发一次，随后必落串行降级。
            if (failoverEnabled && options.Routing.EnableFusionMode && !fusionModeAttempted
                && failedInThisRequest.Count == 0 && !request.Stream
                && decision.Candidates.Count >= 2)
            {
                fusionModeAttempted = true;
                var fusionResult = await _raceOrchestrator.ExecuteAsync(
                    request, options, decision, estimatedTokens, routedTier,
                    sessionId, failedInThisRequest, attemptedModels, effectiveCt).ConfigureAwait(false);

                lastModelName = fusionResult.LastModelName;
                lastStatusCode = fusionResult.LastStatusCode;
                lastErrorMessage = fusionResult.LastErrorMessage;

                if (fusionResult.Response is not null)
                {
                    // 获胜路径同样要记录 regenerate 反馈键（串行降级路径在成功处 Record），
                    // 否则同键重发时负反馈找不到实际产出答案的模型。成功时 LastModelName 必非空。
                    if (fusionResult.LastModelName is not null)
                    {
                        _regenerateTracker.Record(feedbackKey, fusionResult.LastModelName, success: true);
                    }
                    return ProcessResponse(fusionResult.Response, piiMap);
                }
                // 全部失败：failedInThisRequest 已填充，continue 到下一轮串行降级。
                continue;
            }

            bool attemptedCandidate = false;
            foreach (var candidate in decision.Candidates)
            {
                if (!failedInThisRequest.Add(candidate.Name))
                    continue;

                // 断路器放行许可：闭合直接通过；半开占用一个探测槽位；打开或槽位满则跳过本候选。
                // 仅在启用 failover 时做探测门控，与 FailoverPolicy 的排除语义保持一致。
                if (failoverEnabled && !_healthTracker.TryBeginProbe(candidate.Name, halfOpenMaxProbes))
                {
                    _logger.LogInformation("Model {Name} circuit not ready (cooling or probes busy), skipping", candidate.Name);
                    continue;
                }

                attemptedCandidate = true;
                attemptedModels.Add(candidate.Name);

                // outcomeReported：是否已通过 RecordSuccess/RecordFailure 上报结果。
                // 未上报就离开本候选（不可重试异常、外部取消等）时，finally 释放探测槽位，避免泄漏。
                bool outcomeReported = false;
                var attemptSw = System.Diagnostics.Stopwatch.StartNew();
                IDisposable? adaptiveLease = null;
                if (options.Routing.EnableAdaptiveConcurrency)
                {
                    adaptiveLease = await _adaptiveLimiter.AcquireAsync(candidate.Name, effectiveCt).ConfigureAwait(false);
                }
                try
                {
                    var client = _clientProvider.GetClient(candidate);
                    var response = await client.CompleteRawAsync(request, effectiveCt).ConfigureAwait(false);
                    attemptSw.Stop();
                    if (options.Routing.EnableAdaptiveConcurrency)
                    {
                        _adaptiveLimiter.RecordRtt(candidate.Name, attemptSw.Elapsed.TotalMilliseconds);
                    }

                    // MCP 工具执行闭环（默认关闭）：模型请求工具时执行全部 tool_calls 并重放，
                    // 直至无新工具调用或达轮次上限。在成本/延迟统计之前完成，使记账覆盖全部重放轮次。
                    if (options.Routing.EnableMcpToolExecution && !request.Stream && _mcpToolOrchestrator is not null)
                    {
                        response = await _mcpToolOrchestrator.ExecuteToolCallsAndReplayAsync(
                            request, response, candidate, options.Routing.MaxMcpToolRounds,
                            sessionId: sessionId, ct: effectiveCt).ConfigureAwait(false);
                    }

                    // 内容审核（输出）：审核模型生成的最终响应文本，违规按策略中断。
                    if (options.Routing.EnableContentModeration && _contentModerator is not null
                        && options.Routing.ModerationOutputAction != OptiRouter.Compliance.ModerationAction.None
                        && ShouldModerate(options.Routing))
                    {
                        string? outputText = ExtractContentText(response.Body);
                        if (!string.IsNullOrWhiteSpace(outputText))
                        {
                            var modResult = await _contentModerator.ModerateTextAsync(
                                outputText, OptiRouter.Compliance.ModerationDirection.Output, effectiveCt).ConfigureAwait(false);
                            if (modResult.IsViolation)
                            {
                                _logger.LogWarning("Output blocked by content moderation: category={Category}, score={Score:F3}", modResult.Category, modResult.Score);
                                _recorder.RecordAudit(null, "moderation", 0, null, 0m, 0, sessionId, $"moderation-output-blocked:{modResult.Category}", false, modResult.Reason, false, routedTier, requestContent: requestContent);
                                if (options.Routing.ModerationOutputAction == OptiRouter.Compliance.ModerationAction.Block)
                                {
                                    throw new OptiRouter.Compliance.ComplianceViolationException(
                                        $"Output blocked by content moderation (category: {modResult.Category}).", modResult.Category);
                                }
                            }
                        }
                    }

                    decimal cost = response.Usage is not null
                        ? CostCalculator.Compute(response.Usage, candidate)
                        : 0m;
                    // 质量因子：从非流式响应检测低质量信号（截断/空答/JSON 契约违约），乘性折减延迟 reward。
                    // 流式路径未累积 content，不接入（qualityFactor 默认 1.0）。
                    double qualityFactor = OutcomeRecorder.ExtractQualityFactor(response, options.Routing.QualityPenaltyFactor, request);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds, decision, cost,
                        actualTier: candidate.Tier, qualityFactor: qualityFactor, completionTokens: response.Usage?.CompletionTokens ?? 0);
                    outcomeReported = true;
                    _recorder.RecordAffinity(sessionId, candidate.Name, AffinitySignal.Strong, attemptSw.ElapsedMilliseconds);
                    _recorder.RecordPromptCacheAffinity(request, candidate.Name);

                    if (response.Usage is not null)
                    {
                        _recorder.RecordCost(cost, sessionId);
                        _recorder.RecordAudit(null, candidate.Name, estimatedTokens, response.Usage, cost, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, false, routedTier,
                            timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    }
                    else
                    {
                        // 上游未返回 usage：无法精确计费。按估算 input 成本入账并标 IsEstimated，
                        // 与失败/取消路径（RaceOrchestrator/FusionRouter）的估算口径一致，
                        // 避免成功请求被记 0 成本导致日/会话预算低估。
                        decimal estCost = OutcomeRecorder.EstimateInputCost(candidate, estimatedTokens);
                        if (estCost > 0m)
                            _recorder.RecordCost(estCost, sessionId);
                        _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, estCost, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, false, routedTier,
                            isEstimated: estCost > 0m,
                            timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    }
                    _recorder.RecordQuota(candidate.Name, response.Metadata);
                    _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);

                    // LLM-as-judge 采样：按配置采样率把"问题-回答"送打分模型，score 回灌学习状态。
                    // 旁路 fire-and-forget；用 PII 还原前的原文（占位符语义略降但默认脱敏关闭）。
                    if (_qualityJudge is not null)
                    {
                        string judgedAnswer = ResponseConfidenceChecker.ExtractAssistantText(response);
                        if (!string.IsNullOrWhiteSpace(judgedAnswer))
                            _qualityJudge.TryJudge(request, judgedAnswer, candidate.Name, decision, routedTier, sessionId);
                    }

                    // 级联自校验：Cheap 首选 + 启用 + 采样命中 → 自校验，低置信升级 Strong。
                    // 仅非流式（流式首 chunk 已透传无法切模型）。失败不影响主流程，返回原 Cheap 答案。
                    if (candidate.Tier == ModelTier.Cheap)
                    {
                        var upgraded = await _cascadeHandler.TryUpgradeAsync(
                             request, response, decision, candidate, estimatedTokens, routedTier, sessionId, failedInThisRequest, effectiveCt).ConfigureAwait(false);
                        if (upgraded is not null)
                        {
                            // 升级成功：regenerate 反馈与响应缓存都记用户实际看到的 Strong 答案，
                            // 而非被判定低置信、未下发的 Cheap 答案（否则同键重发会惩罚错模型、且升级响应永远缓存 miss）。
                            var upgradedFinal = ProcessResponse(upgraded.Response, piiMap);
                            if (cacheKey is not null)
                                _responseCache.Set(cacheKey, upgradedFinal, TimeSpan.FromSeconds(options.Routing.ResponseCacheTtlSeconds));
                            _regenerateTracker.Record(feedbackKey, upgraded.UpgradedModelName, success: true);
                            return upgradedFinal;
                        }
                    }

                    _logger.LogInformation("Non-streaming request completed: model={Model}, cost={Cost}",
                        candidate.Name, response.Usage is not null
                            ? cost.ToString("F6")
                            : "unknown");

                    var finalResponse = ProcessResponse(response, piiMap);
                    if (cacheKey is not null)
                        _responseCache.Set(cacheKey, finalResponse, TimeSpan.FromSeconds(options.Routing.ResponseCacheTtlSeconds));
                    if (CanUseSemanticCache(options.Routing, request, piiMap) && !request.Stream)
                    {
                        string? promptText = GetLastUserPrompt(request);
                        if (!string.IsNullOrWhiteSpace(promptText))
                        {
                            string semanticPartition = BuildSemanticPartition(request, securityPartition);
                            // 存储未还原的原始响应（含 PII 占位符）：命中时由当前请求 piiMap 还原，
                            // 保证不同用户相同结构请求共享缓存项时各自得到自己的 PII 值。
                            await _semanticCache.StoreAsync(
                                promptText,
                                response,
                                TimeSpan.FromMinutes(options.Routing.SemanticCacheTtlMinutes),
                                ct,
                                partitionKey: semanticPartition).ConfigureAwait(false);
                        }
                    }
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: true);
                    return finalResponse;
                }
                catch (ModelClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = 429;
                    lastErrorMessage = "quota-exhausted";
                    _recorder.RecordQuota(candidate.Name, ex.Metadata, rateLimited: true);
                    _healthTracker.ReleaseProbe(candidate.Name);
                    outcomeReported = true;
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m,
                        attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "quota-exhausted", false,
                        routedTier, quotaLimited: true, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    _logger.LogWarning("Model {Name} quota exhausted (status {Status}), trying next candidate",
                        candidate.Name, 429);
                }
                catch (ModelClientException ex) when (IsRequestRejection(ex))
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = (int)ex.StatusCode;
                    lastErrorMessage = $"upstream-status-{(int)ex.StatusCode}";
                    // 请求语义类拒绝（400/422/413...）：上游校验阶段即拒绝，未产生生成费用，
                    // 降级尝试其余候选成本≈0。不熔断（模型对其他请求仍可用），但必须进审计
                    // 与 bandit——此前此类失败对学习回路完全不可见（审计零记录、路由反复踩坑）。
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    outcomeReported = true;
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false,
                        lastErrorMessage, false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _healthTracker.ReleaseProbe(candidate.Name);
                    bool rejectHasOther = HasOtherCandidate(decision, candidate.Name, failedInThisRequest);
                    _logger.LogWarning("Model {Name} rejected request (status {Status}){Action}",
                        candidate.Name, ex.StatusCode, rejectHasOther ? ", trying next candidate" : ", propagating to client");
                    if (!rejectHasOther)
                        throw; // 无候选可降级：保持透传语义，原始状态码到达客户端
                }
                catch (ModelClientException ex) when (IsRetryable(ex) || (IsCredentialError(ex) && HasOtherCandidate(decision, candidate.Name, failedInThisRequest)))
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = (int)ex.StatusCode;
                    lastErrorMessage = $"upstream-status-{(int)ex.StatusCode}";
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    outcomeReported = true;
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false,
                        $"upstream-status-{(int)ex.StatusCode}", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _logger.LogWarning("Model {Name} failed (status {Status}), trying next candidate{Tripped}",
                        candidate.Name, ex.StatusCode, tripped ? " (circuit tripped)" : "");
                }
                catch (HttpRequestException ex)
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = 503;
                    lastErrorMessage = "network-error";
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    outcomeReported = true;
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "network-error", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _logger.LogWarning(ex, "Model {Name} network request failed, trying next candidate{Tripped}",
                        candidate.Name, tripped ? " (circuit tripped)" : "");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = 408;
                    bool isGlobalTimeout = globalCts is { IsCancellationRequested: true };
                    lastErrorMessage = isGlobalTimeout
                        ? $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded."
                        : "Request timed out inside the proxy.";
                    // 客户端内部超时/全局 Failover 超时，非外部取消，记失败继续。
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    outcomeReported = true;
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, isGlobalTimeout ? "global-failover-timeout" : "timeout", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _logger.LogWarning("Model {Name} timed out ({Reason}), trying next{Tripped}",
                        candidate.Name, isGlobalTimeout ? "global failover timeout" : "timeout", tripped ? " (circuit tripped)" : "");

                    if (isGlobalTimeout)
                    {
                        throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.");
                    }
                }
                finally
                {
                    adaptiveLease?.Dispose();
                    if (!outcomeReported)
                        _healthTracker.ReleaseProbe(candidate.Name);
                }
            }

            if (!failoverEnabled || !attemptedCandidate)
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, "All candidates failed.");
        }
    }

    /// <summary>
    /// 流式发送请求，按候选链顺序尝试。首个 chunk 开始 yield 后若失败，
    /// 无法再切换模型，直接向上抛出异常。透传上游原始 SSE data 行。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="sessionId">可选会话 ID，用于按会话记账。</param>
    /// <returns>原始 SSE 行异步枚举。</returns>
    /// <exception cref="BudgetExhaustedException">预算耗尽且模式为 Reject。</exception>
    /// <exception cref="AllCandidatesFailedException">所有候选在首 chunk 前均失败。</exception>
    public async IAsyncEnumerable<RawStreamLine> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct, string? sessionId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        // 会话兜底：无 X-Session-Id 时从对话内容派生（须在 PII/压缩改写之前）。
        sessionId ??= DeriveConversationSession(request);
        sessionId = ScopeSessionId(sessionId);

        var options = _options.CurrentValue;
        var failedInThisRequest = new HashSet<string>(StringComparer.Ordinal);
        var attemptedModels = new List<string>();
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;
        int halfOpenMaxProbes = options.Routing.FailoverHalfOpenMaxProbes;
        int halfOpenRequiredSuccesses = options.Routing.FailoverHalfOpenRequiredSuccesses;
        bool failoverEnabled = options.Routing.EnableFailover;

        using var globalCts = (failoverEnabled && options.Routing.FailoverGlobalTimeoutSeconds > 0)
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (globalCts is not null)
        {
            globalCts.CancelAfter(TimeSpan.FromSeconds(options.Routing.FailoverGlobalTimeoutSeconds));
        }
        var effectiveCt = globalCts?.Token ?? ct;

        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;
        long totalBytesTransferred = 0;
        long maxResponseBytes = options.Routing.MaxResponseStreamBytes;
        bool fusionRouterAttempted = false;
        StreamingSlidingWindowBuffer? complianceBuffer = options.Routing.EnableStreamingComplianceFilter
            ? new StreamingSlidingWindowBuffer()
            : null;

        // PII 脱敏（与非流式 SendAsync 对称）：流式路径同样必须在上游发送前替换敏感数据，
        // 并在每个 yield 行上反向还原，否则原始 PII 直达上游、占位符泄露给客户端。
        // regenerate 负反馈键：必须在脱敏前基于原始请求计算（脱敏占位符相同的不同请求会串扰键）。
        string securityPartition = BuildSecurityPartition(sessionId);
        string? feedbackKey = options.Routing.EnableRegenerateFeedback
            ? BuildPartitionedCacheKey(securityPartition, ResponseCacheKey.Compute(request))
            : null;
        bool regeneratePenaltyApplied = false;

        PiiMap? piiMap = null;
        if (options.Routing.EnablePiiAnonymization)
        {
            var anonymized = PiiAnonymizer.AnonymizeRequest(request);
            request = anonymized.SanitizedRequest;
            piiMap = anonymized.PiiMap;
        }

        // 构造请求内容摘要（用于 dashboard 展示），根据开关决定是否提取
        string? requestContent = options.Routing.AuditStoreRequestContent
            ? ExtractRequestContentSummary(request)
            : null;

        // Persona 锚定（与非流式 SendAsync 对称）：流式路径同样在上游发送前注入锚定，
        // 否则同一开关在流式请求上静默失效。
        if (options.Routing.EnablePersonaDriftProtection && !string.IsNullOrEmpty(sessionId))
        {
            request = PersonaDriftGuard.ApplyPersonaAnchor(request);
        }

        if (options.Routing.EnablePromptCompression)
        {
            // 模式联动与非流式路径一致（压缩先于路由决策，静态解析模式预设）。
            var compression = RoutingModePolicy.AdjustCompression(
                options.Routing.PromptCompression, RoutingModePolicy.TryResolveMode(request.Model));
            var compResult = _promptPruner.Compress(request, compression);
            if (compResult.WasCompressed)
            {
                request = compResult.CompressedRequest;
            }
        }

        // 内容审核（输入）：流式路径仅审核用户输入（输出增量审核由流式敏感词过滤器承担）。
        if (options.Routing.EnableContentModeration && _contentModerator is not null && ShouldModerate(options.Routing))
        {
            string? userText = GetLastUserPrompt(request);
            if (!string.IsNullOrWhiteSpace(userText))
            {
                var modResult = await _contentModerator.ModerateTextAsync(userText, OptiRouter.Compliance.ModerationDirection.Input, effectiveCt).ConfigureAwait(false);
                if (modResult.IsViolation)
                {
                    _logger.LogWarning("Streaming input blocked by content moderation: category={Category}, score={Score:F3}", modResult.Category, modResult.Score);
                    _recorder.RecordAudit(null, "moderation", 0, null, 0m, 0, sessionId, $"moderation-input-blocked:{modResult.Category}", false, modResult.Reason, false, ModelTier.Cheap, requestContent: requestContent);
                    if (options.Routing.ModerationInputAction == OptiRouter.Compliance.ModerationAction.Block)
                    {
                        throw new OptiRouter.Compliance.ComplianceViolationException(
                            $"Input blocked by content moderation (category: {modResult.Category}).", modResult.Category);
                    }
                }
            }
        }

        // in-flight 预算预留作用域：using var 在迭代器提前 Dispose（客户端断开）时也保证释放。
        using var budgetReservation = new BudgetReservationScope(_recorder, sessionId);

        while (true)
        {
            if (globalCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode ?? 408, lastErrorMessage ?? $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.", $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.");
            }

            var decision = _engine.Decide(request, options, failedInThisRequest, sessionId);
            ModelTier routedTier = decision.Candidates.Count > 0 ? decision.Candidates[0].Tier : ModelTier.Medium;

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Route decision: {Reason}, candidates=[{Names}]",
                    decision.Reason, string.Join(", ", decision.Candidates.Select(c => c.Name)));

            if (decision.Candidates.Count == 0)
            {
                if (decision.BudgetExhausted)
                {
                    // 预算耗尽拒绝也要留审计痕：请求未到上游、不走正常落账路径，
                    // 不记则租户被拒事件在 Requests/Dashboard 完全不可见。
                    _recorder.RecordAudit(null, "budget-guard", decision.EstimatedInputTokens, null, 0m, 0, sessionId,
                        decision.Reason, false, "budget exhausted", false, routedTier, classificationSignal: decision.ClassificationSignal);
                    throw new BudgetExhaustedException(decision.Reason);
                }
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, decision.Reason);
            }

            // 预算预扣：本轮决策已过守卫，立即占用本请求预估成本，使后续并发请求的守卫
            // 看到"已入账 + 全部 in-flight"。Dispose（含迭代器提前终止）释放预留，真实计费后净额即实际。
            // 门控只看旋钮不看 EnableBudgetGuard：租户级预算（ClientKeyService）独立于全局守卫执行，
            // 预留经 OutcomeRecorder 同时覆盖全局账本与授权租户两个维度。
            if (options.Budget.ReservationMaxOutputTokens > 0)
            {
                budgetReservation.Arm(EstimateReservationCost(
                    decision.Candidates[0], decision.EstimatedInputTokens, request, options.Budget.ReservationMaxOutputTokens));
            }

            // regenerate 负反馈（与非流式对称）：同键请求窗口内重发且上次成功 → 惩罚上次模型。
            if (!regeneratePenaltyApplied)
            {
                regeneratePenaltyApplied = true;
                if (feedbackKey is not null
                    && _regenerateTracker.TryConsumeRegenerate(
                        feedbackKey, TimeSpan.FromSeconds(options.Routing.RegenerateFeedbackWindowSeconds), out string previousModel))
                {
                    _recorder.RecordQualityOutcome(previousModel, options.Routing.RegeneratePenaltyReward, decision);
                    _logger.LogInformation(
                        "Regenerate feedback: penalizing previous model {Model} with reward {Reward:0.00}",
                        previousModel, options.Routing.RegeneratePenaltyReward);
                }
            }

            // 融合路由流式支持（Progressive Speculative Streaming）
            if (failoverEnabled && options.Routing.EnableFusionRouter && !fusionRouterAttempted
                && failedInThisRequest.Count == 0
                && decision.RequestComplexity >= options.Routing.FusionRouterMinComplexity
                && decision.Candidates.Count >= 2)
            {
                fusionRouterAttempted = true;
                bool producedAnyChunk = false;
                await foreach (var line in _fusionRouter.ExecuteStreamAsync(
                    request, options, decision, decision.EstimatedInputTokens, routedTier,
                    sessionId, failedInThisRequest, attemptedModels, effectiveCt).WithCancellation(effectiveCt))
                {
                    producedAnyChunk = true;
                    var restored = ProcessCompliance(RestorePii(line, piiMap), complianceBuffer, options.Routing);
                    totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(restored.Data ?? "");
                    if (totalBytesTransferred > maxResponseBytes)
                    {
                        throw new ResponseSizeLimitExceededException(maxResponseBytes,
                            $"Response size limit exceeded ({maxResponseBytes} bytes).");
                    }
                    yield return restored;
                }

                if (producedAnyChunk)
                    yield break;
            }

            bool attemptedCandidate = false;
            foreach (var candidate in decision.Candidates)
            {
                if (!failedInThisRequest.Add(candidate.Name))
                    continue;

                // 断路器放行许可（与非流式一致）：半开占用探测槽位，打开/槽位满则跳过。
                if (failoverEnabled && !_healthTracker.TryBeginProbe(candidate.Name, halfOpenMaxProbes))
                {
                    _logger.LogInformation("Model {Name} circuit not ready (cooling or probes busy), skipping", candidate.Name);
                    continue;
                }

                attemptedCandidate = true;
                attemptedModels.Add(candidate.Name);
                var client = _clientProvider.GetClient(candidate);
                IAsyncEnumerator<RawStreamLine>? enumerator = null;
                RawStreamLine firstLine = default!;
                // LLM-as-judge 流式质量采样：逐行累积 delta 文本，流正常结束后送审（旁路 fire-and-forget）。
                var judgeTextSb = new System.Text.StringBuilder();
                ChatUsage? finalUsage = null;
                Exception? preStreamFailure = null;
                bool hasFirstLine = false;
                // probeResolved：已上报成功/失败结果；streamFaulted：首行后流中途异常中断。
                // 两者共同保证离开本候选时探测槽位必被结算（上报或释放），不泄漏。
                bool probeResolved = false;
                bool streamFaulted = false;
                var attemptSw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Phase 1: 创建 enumerator 并尝试拿到第一行。
                    // 此处有 catch，不能 yield；仅做"失败则继续下一候选"的判定。
                    try
                    {
                        // TTFT 专项超时：首字节迟迟不到时尽快 failover，而非干等到整体超时。
                        // 超时抛 OperationCanceledException，落入下方 !ct.IsCancellationRequested 的 catch（记断路器 + continue）。
                        CancellationTokenSource? ttftCts = options.Routing.StreamFirstTokenTimeoutMs > 0
                            ? CancellationTokenSource.CreateLinkedTokenSource(effectiveCt)
                            : null;
                        try
                        {
                            if (ttftCts is not null)
                                ttftCts.CancelAfter(TimeSpan.FromMilliseconds(options.Routing.StreamFirstTokenTimeoutMs));
                            var firstTokenCt = ttftCts?.Token ?? effectiveCt;

                            enumerator = client.StreamRawAsync(request, firstTokenCt).GetAsyncEnumerator(firstTokenCt);
                            if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                firstLine = enumerator.Current;
                                _recorder.RecordQuota(candidate.Name, firstLine.Metadata);
                                if (firstLine.Usage is not null)
                                    finalUsage = firstLine.Usage;
                                hasFirstLine = true;
                                if (_qualityJudge is not null)
                                {
                                    string? firstDelta = ExtractDeltaText(firstLine.Data);
                                    if (!string.IsNullOrEmpty(firstDelta))
                                        judgeTextSb.Append(firstDelta);
                                }
                            }
                            else
                            {
                                // 空流：视为成功但无内容，继续尝试下一个候选。
                                await enumerator.DisposeAsync().ConfigureAwait(false);
                                enumerator = null;
                                // 无健康信号：外层 finally 会释放探测槽位。
                                continue;
                            }
                        }
                        finally
                        {
                            ttftCts?.Dispose();
                        }
                    }
                    catch (ModelClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = 429;
                        lastErrorMessage = "quota-exhausted";
                    }
                    catch (ModelClientException ex) when (IsRequestRejection(ex))
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = (int)ex.StatusCode;
                        lastErrorMessage = $"upstream-status-{(int)ex.StatusCode}";
                    }
                    catch (ModelClientException ex) when (IsRetryable(ex) || (IsCredentialError(ex) && HasOtherCandidate(decision, candidate.Name, failedInThisRequest)))
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = (int)ex.StatusCode;
                        lastErrorMessage = $"upstream-status-{(int)ex.StatusCode}";
                    }
                    catch (HttpRequestException ex)
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = 503;
                        lastErrorMessage = "network-error";
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = 408;
                        bool isGlobalTimeout = globalCts is { IsCancellationRequested: true };
                        lastErrorMessage = isGlobalTimeout
                            ? $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded."
                            : "Request timed out inside the proxy.";
                    }
                    finally
                    {
                        if (!hasFirstLine && enumerator is not null)
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                        }
                    }

                    if (preStreamFailure is not null)
                    {
                        attemptSw.Stop();
                        bool quotaLimited = preStreamFailure is ModelClientException
                        { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                        // 请求语义类拒绝（400/422/413...）：与配额/可重试并列的第三类——
                        // 不熔断（模型对其他请求仍可用），但进审计与 bandit；无候选可降级时透传。
                        bool requestRejection = !quotaLimited
                            && preStreamFailure is ModelClientException rejectionEx
                            && IsRequestRejection(rejectionEx);
                        bool tripped = false;
                        double? preStreamReward = null;
                        if (quotaLimited)
                        {
                            var quotaError = (ModelClientException)preStreamFailure;
                            _recorder.RecordQuota(candidate.Name, quotaError.Metadata, rateLimited: true);
                            _healthTracker.ReleaseProbe(candidate.Name);
                        }
                        else if (requestRejection)
                        {
                            preStreamReward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                            _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                            _healthTracker.ReleaseProbe(candidate.Name);
                        }
                        else
                        {
                            tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                            preStreamReward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                            _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                        }
                        probeResolved = true;
                        bool isGlobalTimeout = globalCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested;
                        string failure = quotaLimited
                            ? "quota-exhausted"
                            : preStreamFailure is ModelClientException modelFailure
                            ? $"upstream-status-{(int)modelFailure.StatusCode}"
                            : isGlobalTimeout
                            ? "global-failover-timeout"
                            : preStreamFailure.Message;
                        _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                            attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, failure, true, routedTier,
                            quotaLimited: quotaLimited,
                            reward: preStreamReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                        _logger.LogWarning("Streaming model {Name} failed pre-stream ({Failure}), trying next{Tripped}",
                            candidate.Name, failure, tripped ? " (circuit tripped)" : "");

                        if (isGlobalTimeout)
                        {
                            throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.");
                        }
                        if (requestRejection && !HasOtherCandidate(decision, candidate.Name, failedInThisRequest))
                        {
                            // 无候选可降级：透传原始 4xx 给客户端（端点包装为 UPSTREAM_REJECTION）。
                            throw preStreamFailure;
                        }
                        continue;
                    }

                    ArgumentNullException.ThrowIfNull(enumerator);

                    // Phase 2: 首行与剩余行统一在内层 try-finally 内 yield（无 catch，CS1626 允许）。
                    // size-limit 抛出时 finally 仍会 dispose enumerator，避免 socket/stream 泄漏。
                    try
                    {
                        // 先还原 PII 再统计字节：限流须约束实际下发客户端的字节数，
                        // 否则占位符还原为（更长的）原文后实际体量可超过 MaxResponseStreamBytes。
                        var restoredFirst = ProcessCompliance(RestorePii(firstLine, piiMap), complianceBuffer, options.Routing);
                        totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(restoredFirst.Data ?? "");
                        if (totalBytesTransferred > maxResponseBytes)
                        {
                            throw new ResponseSizeLimitExceededException(maxResponseBytes,
                                $"Response size limit exceeded ({maxResponseBytes} bytes).");
                        }
                        yield return restoredFirst;

                        // 继续 yield 剩余行。
                        while (true)
                        {
                            bool moved;
                            try
                            {
                                moved = await enumerator!.MoveNextAsync().ConfigureAwait(false);
                            }
                            catch
                            {
                                // 流中途异常中断（上游断连/超时等）：标记后向外抛出。
                                streamFaulted = true;
                                throw;
                            }

                            if (!moved)
                                break;

                            var line = enumerator.Current;
                            if (line.Usage is not null)
                                finalUsage = line.Usage;
                            if (_qualityJudge is not null && !string.IsNullOrEmpty(line.Data))
                            {
                                string? judgeDelta = ExtractDeltaText(line.Data);
                                if (!string.IsNullOrEmpty(judgeDelta))
                                    judgeTextSb.Append(judgeDelta);
                            }

                            var restored = ProcessCompliance(RestorePii(line, piiMap), complianceBuffer, options.Routing);
                            totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(restored.Data ?? "");
                            if (totalBytesTransferred > maxResponseBytes)
                            {
                                throw new ResponseSizeLimitExceededException(maxResponseBytes,
                                    $"Response size limit exceeded ({maxResponseBytes} bytes).");
                            }
                            yield return restored;
                        }

                        // 流结束：补发 Redact 模式为跨 chunk 匹配而暂存的尾部字符（窗口关闭，前缀不可能再补全为敏感词）。
                        if (complianceBuffer is not null)
                        {
                            string pendingTail = _complianceFilter.FlushRemaining(complianceBuffer);
                            if (!string.IsNullOrEmpty(pendingTail))
                            {
                                yield return new RawStreamLine(
                                    ReplaceDeltaContent("{\"choices\":[{\"index\":0,\"delta\":{}}]}", pendingTail), null, null);
                            }
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }

                    // 流正常结束，记账 + 标记健康。没有 usage 时按输入 token 估算，
                    // 避免成功请求被记为零成本，并在审计中保留预估标记。
                    decimal cost = finalUsage is not null
                        ? CostCalculator.Compute(finalUsage, candidate)
                        : OutcomeRecorder.EstimateInputCost(candidate, decision.EstimatedInputTokens);
                    bool isEstimated = finalUsage is null;
                    if (!isEstimated || cost > 0m)
                        _recorder.RecordCost(cost, sessionId);
                    _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);
                    // 流式质量采样：累积全文非空才派发（与融合 patch chunk 一样，judge 只看正文）。
                    if (_qualityJudge is not null && judgeTextSb.Length > 0)
                        _qualityJudge.TryJudge(request, judgeTextSb.ToString(), candidate.Name, decision, routedTier, sessionId);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds, decision, cost,
                        actualTier: candidate.Tier, completionTokens: finalUsage?.CompletionTokens ?? 0, timeToFirstTokenMs: firstLine.Metadata?.TimeToFirstTokenMs);
                    _recorder.RecordAffinity(sessionId, candidate.Name, AffinitySignal.Strong, attemptSw.ElapsedMilliseconds);
                    _recorder.RecordPromptCacheAffinity(request, candidate.Name);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: true);
                    probeResolved = true;
                    attemptSw.Stop();
                    _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, finalUsage,
                        cost,
                        attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, true, routedTier,
                        isEstimated: isEstimated,
                        timeToFirstTokenMs: firstLine.Metadata?.TimeToFirstTokenMs,
                        reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                    _logger.LogInformation("Streaming request completed: model={Model}, cost={Cost}",
                        candidate.Name, cost.ToString("F6"));
                    yield break;
                }
                finally
                {
                    if (!probeResolved)
                    {
                        if (streamFaulted)
                        {
                            // 中途失败计入断路器统计（与非流式失败同等对待）。
                            attemptSw.Stop();
                            bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                            double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
                            _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                            _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                                attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "stream-faulted", true, routedTier,
                                reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent, classificationSignal: decision.ClassificationSignal);
                            _logger.LogWarning("Streaming model {Name} failed mid-stream{Tripped}",
                                candidate.Name, tripped ? " (circuit tripped)" : "");
                        }
                        else
                        {
                            // 无健康信号（不可重试错误、外部取消、空流、客户端提前断开）：仅释放探测槽位。
                            _healthTracker.ReleaseProbe(candidate.Name);
                        }
                    }
                }
            }

            if (!failoverEnabled || !attemptedCandidate)
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, "All candidates failed.");
        }
    }

    /// <summary>
    /// 释放共享的 SocketsHttpHandler 和客户端提供者。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_clientProvider is IDisposable d) d.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_clientProvider is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else if (_clientProvider is IDisposable d) d.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// in-flight 预算预留作用域：构造后按需 Arm 一次预扣额，Dispose（含流式迭代器提前 Dispose，
    /// 即客户端断开）时释放。using var 形式让编译器生成的 finally 覆盖所有提前终止路径。
    /// </summary>
    private sealed class BudgetReservationScope : IDisposable
    {
        private readonly OutcomeRecorder _recorder;
        private readonly string? _sessionId;
        private decimal _amount;
        private bool _armed;

        public BudgetReservationScope(OutcomeRecorder recorder, string? sessionId)
        {
            _recorder = recorder;
            _sessionId = sessionId;
        }

        /// <summary>首轮路由决策后预扣。幂等：failover 后续轮次不重估（换模型价格差异属可接受近似）。</summary>
        public void Arm(decimal amount)
        {
            if (_armed || amount <= 0m) return;
            _armed = true;
            _amount = amount;
            _recorder.ReserveCostEstimate(amount, _sessionId);
        }

        public void Dispose()
        {
            if (_amount > 0m)
            {
                _recorder.ReleaseCostEstimate(_amount, _sessionId);
            }
        }
    }

    /// <summary>
    /// 预算预留估算：输入估算成本 + 输出预估成本（min(请求 max_tokens, 配置上限)）。
    /// 融合路由（panel 并行 ×N）按单请求口径预留会低估，属已知近似。
    /// </summary>
    private static decimal EstimateReservationCost(
        ModelEndpointOptions candidate, int estimatedInputTokens, ChatRequest request, int reservationMaxOutputTokens)
    {
        decimal inputCost = OutcomeRecorder.EstimateInputCost(candidate, estimatedInputTokens);
        int maxOutput = request.MaxTokens is { } mt
            ? Math.Min(mt, reservationMaxOutputTokens)
            : reservationMaxOutputTokens;
        return inputCost + maxOutput * candidate.OutputPricePerMillion / 1_000_000m;
    }

    /// <summary>
    /// 可重试的失败状态码。429 不在此列：流式与非流式路径都在 <c>ModelClientException</c>
    /// 独立 catch 分支（<c>StatusCode == TooManyRequests</c>）先行捕获，走配额/健康隔离路径，
    /// 不到达这里。本方法只处理真正的可重试上游故障（408 / 5xx）。
    /// </summary>
    private static bool IsRetryable(ModelClientException exception)
    {
        int statusCode = (int)exception.StatusCode;
        return statusCode is 408 or >= 500 and <= 599;
    }

    /// <summary>
    /// 凭证/权限类上游错误（401/403）：该模型连接配置的问题，与请求内容无关——
    /// 换一候选可能成功（如某 key 失效而其余模型健康）。
    /// 其余 4xx 为请求语义拒绝，由 <see cref="IsRequestRejection"/> 独立分支处理：
    /// 有其他候选时降级（上游校验阶段拒绝、无生成费用），无候选时透传原始状态码。
    /// </summary>
    private static bool IsCredentialError(ModelClientException exception)
    {
        int statusCode = (int)exception.StatusCode;
        return statusCode is 401 or 403;
    }

    /// <summary>
    /// 请求语义类上游拒绝（400/413/422 等）：上游在校验阶段即拒绝，未产生生成费用。
    /// 与 429（配额独立分支）、401/403（凭证）、408/5xx（可重试）互斥。
    /// </summary>
    private static bool IsRequestRejection(ModelClientException exception)
    {
        int statusCode = (int)exception.StatusCode;
        return statusCode is >= 400 and <= 499
            and not 401 and not 403 and not 408 and not 429;
    }

    /// <summary>
    /// 是否还有未失败的其他候选。凭证错误在 auto 路由下应降级到下一候选而非放弃整个请求；
    /// 无其他候选（显式单模型或最后一个候选）时保持透传，原始状态码到达客户端。
    /// </summary>
    private static bool HasOtherCandidate(RouterDecision decision, string currentModel, HashSet<string> failed) =>
        decision.Candidates.Any(c =>
            !string.Equals(c.Name, currentModel, StringComparison.Ordinal)
            && !failed.Contains(c.Name));

    private static RawChatResponse ProcessResponse(RawChatResponse response, PiiMap? piiMap)
    {
        if (piiMap is null || !piiMap.HasSensitiveData || string.IsNullOrEmpty(response.Body))
            return response;

        string restoredBody = piiMap.Restore(response.Body);
        return new RawChatResponse(restoredBody, response.Usage, response.Metadata);
    }

    private static RawStreamLine RestorePii(RawStreamLine line, PiiMap? piiMap)
    {
        if (piiMap is null || !piiMap.HasSensitiveData || string.IsNullOrEmpty(line.Data))
            return line;

        return line with { Data = piiMap.Restore(line.Data) };
    }

    private static string? GetLastUserPrompt(ChatRequest request)
    {
        if (request?.Messages is null) return null;
        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                var text = msg.GetText();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a cache security partition without ever including a bearer secret.
    /// Tenant requests use the immutable key id attached by the authentication middleware;
    /// authenticated dashboard requests use the shared admin principal; all remaining proxy
    /// requests use the shared global principal. Session affinity is scoped within that principal.
    /// </summary>
    private string BuildSecurityPartition(string? sessionId)
    {
        var context = _httpContextAccessor?.HttpContext;
        string principal;

        if (context?.Items[typeof(ClientKeyAuthorizationResult)] is ClientKeyAuthorizationResult authorization
            && authorization.IsAuthorized
            && !string.IsNullOrWhiteSpace(authorization.KeyId))
        {
            principal = $"client:{authorization.KeyId}";
        }
        else if (context?.User.Identity?.IsAuthenticated == true)
        {
            principal = "admin";
        }
        else
        {
            // A request reaching ProxyOrchestrator without a tenant identity is the global proxy key
            // path (or an internal/test invocation). Never use the bearer value itself as a partition.
            principal = "global";
        }

        string session = sessionId ?? string.Empty;
        return $"principal={principal.Length}:{principal};session={session.Length}:{session}";
    }

    /// <summary>
    /// 会话兜底：客户端未发 <c>X-Session-Id</c> 时，从对话内容派生稳定会话指纹——
    /// 取首条 user 消息文本的 SHA256 前 16 hex（跳过 system：部分 agent 每轮向 system 注入动态时间戳）。
    /// 同一会话各轮次共享首条 user 消息 → 指纹稳定；新任务首条 user 变化 → 自然区分。
    /// <para>
    /// 背景：agent 客户端（如 omp）普遍不带会话头，会话亲和、会话预算、审计会话维度全部空转
    /// （session_spend 0 行）。派生须在 PII/压缩等改写之前，且基于首条消息——裁剪只动历史尾部。
    /// </para>
    /// </summary>
    private static string? DeriveConversationSession(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return null;

        string? anchor = null;
        foreach (var message in request.Messages)
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                string text = message.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    anchor = text;
                    break;
                }
            }
        }

        // 无 user 消息（如纯 system 预热请求）：退回首条非空消息文本。
        anchor ??= request.Messages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.GetText()))?.GetText();
        if (string.IsNullOrEmpty(anchor))
            return null;

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(anchor));
        return "conv-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private string? ScopeSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return sessionId;

        var context = _httpContextAccessor?.HttpContext;
        if (context?.Items[typeof(ClientKeyAuthorizationResult)] is ClientKeyAuthorizationResult authorization
            && authorization.IsAuthorized
            && !string.IsNullOrWhiteSpace(authorization.KeyId))
        {
            string keyId = authorization.KeyId;
            return $"client={keyId.Length}:{keyId};session={sessionId.Length}:{sessionId}";
        }

        return sessionId;
    }

    private static string BuildPartitionedCacheKey(string securityPartition, string requestKey) =>
        $"{securityPartition}|request={requestKey}";

    /// <summary>
    /// Semantic matching is intentionally conservative: moderation must always see the request and
    /// response, sensitive PII must not be shared by similarity, and tool conversations are
    /// context-sensitive even when their latest user text is identical.
    /// </summary>
    private static bool CanUseSemanticCache(RoutingOptions routing, ChatRequest request, PiiMap? piiMap) =>
        routing.EnableSemanticCache
        && !routing.EnableContentModeration
        && piiMap?.HasSensitiveData != true
        && !ContainsToolContext(request);

    private static bool ContainsToolContext(ChatRequest request)
    {
        if (ContainsToolExtension(request.ExtensionData))
            return true;

        foreach (var message in request.Messages ?? [])
        {
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Role, "function", StringComparison.OrdinalIgnoreCase)
                || ContainsToolExtension(message.ExtensionData))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsToolExtension(IDictionary<string, System.Text.Json.JsonElement>? extensionData)
    {
        if (extensionData is null)
            return false;

        return extensionData.Keys.Any(key =>
            key.Equals("tools", StringComparison.OrdinalIgnoreCase)
            || key.Equals("tool_choice", StringComparison.OrdinalIgnoreCase)
            || key.Equals("tool_calls", StringComparison.OrdinalIgnoreCase)
            || key.Equals("tool_call_id", StringComparison.OrdinalIgnoreCase)
            || key.Equals("function_call", StringComparison.OrdinalIgnoreCase)
            || key.Equals("functions", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds the complete request context to the semantic partition while removing only the text
    /// used for similarity matching. Model parameters, system/history messages and tool metadata
    /// therefore remain part of the partition.
    /// </summary>
    private static string BuildSemanticPartition(ChatRequest request, string securityPartition)
    {
        var messages = request.Messages?.ToList() ?? [];
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                messages[i] = messages[i] with { Content = null };
                break;
            }
        }

        var contextRequest = request with { Messages = messages };
        return BuildPartitionedCacheKey(securityPartition, ResponseCacheKey.Compute(contextRequest));
    }

    /// <summary>
    /// 采样判定：采样率 >= 1.0 全量审核，否则按概率抽样（控制审核 API 成本）。
    /// </summary>
    private static bool ShouldModerate(Configuration.RoutingOptions routing) =>
        routing.ModerationSampleRate >= 1.0
        || Random.Shared.NextDouble() < Math.Clamp(routing.ModerationSampleRate, 0.0, 1.0);

    /// <summary>
    /// 从 OpenAI 兼容响应 JSON 提取 choices[0].message.content 文本（用于输出审核）。
    /// </summary>
    private static string? ExtractContentText(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != System.Text.Json.JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }
            var message = choices[0];
            if (!message.TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("content", out var content)
                || content.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }
            return content.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private RawStreamLine ProcessCompliance(RawStreamLine line, StreamingSlidingWindowBuffer? buffer, RoutingOptions routingOpts)
    {
        if (buffer == null || !routingOpts.EnableStreamingComplianceFilter || string.IsNullOrEmpty(line.Data))
            return line;

        string? delta = ExtractDeltaText(line.Data);
        if (string.IsNullOrEmpty(delta))
            return line;

        var result = _complianceFilter.ProcessChunk(delta, buffer);
        if (result.IsViolation)
        {
            if (routingOpts.StreamingComplianceAction == ComplianceAction.Block)
            {
                _logger.LogWarning("Streaming compliance violation intercepted: keyword={Keyword}", result.MatchedKeyword);
                throw new ComplianceViolationException($"Streaming content blocked due to sensitive keyword match ({result.MatchedKeyword}).", result.MatchedKeyword);
            }
            else if (routingOpts.StreamingComplianceAction == ComplianceAction.Redact)
            {
                string redactedData = ReplaceDeltaContent(line.Data, result.ProcessedText);
                return new RawStreamLine(redactedData, line.Usage, line.Metadata);
            }
        }

        return line;
    }

    private static string? ExtractDeltaText(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Trim() == "[DONE]")
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return content.GetString();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string ReplaceDeltaContent(string data, string newDelta)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(data) is System.Text.Json.Nodes.JsonObject node)
            {
                if (node["choices"]?[0]?["delta"] is System.Text.Json.Nodes.JsonObject deltaObj)
                {
                    deltaObj["content"] = newDelta;
                    return node.ToJsonString();
                }
            }
        }
        catch
        {
        }
        return data;
    }

    /// <summary>
    /// 提取请求内容摘要（用于 Dashboard 展示）。
    /// 取最后一条非空 user 消息文本，截断到 500 字符。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <returns>请求内容摘要，无 user 消息时返回 null。</returns>
    internal static string? ExtractRequestContentSummary(ChatRequest request)
    {
        if (request is null || request.Messages is null)
            return null;

        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg.Role == "user")
            {
                var text = msg.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Length > 500 ? text.Substring(0, 500) + "..." : text;
                }
            }
        }
        return null;
    }
}
