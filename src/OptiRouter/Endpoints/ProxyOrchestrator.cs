using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        IStreamingComplianceFilter? complianceFilter = null)
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
        string? cacheKey = options.Routing.EnableResponseCache && !request.Stream
            ? ResponseCacheKey.Compute(request)
            : null;

        // regenerate 负反馈键：与响应缓存同源（规范化请求 SHA256），必须在 PII 脱敏前基于原始请求计算
        // （脱敏占位符相同的不同请求会串扰键）。
        string? feedbackKey = options.Routing.EnableRegenerateFeedback
            ? cacheKey ?? ResponseCacheKey.Compute(request)
            : null;

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

        // 深度语义向量响应缓存 (Semantic Cache) 尝试相似度匹配
        if (options.Routing.EnableSemanticCache && !request.Stream)
        {
            string? promptText = GetLastUserPrompt(request);
            if (!string.IsNullOrWhiteSpace(promptText))
            {
                var (semHit, semCached, semSim, matchedPrompt) = await _semanticCache.TryGetAsync(
                    promptText,
                    options.Routing.SemanticCacheSimilarityThreshold,
                    ct).ConfigureAwait(false);

                if (semHit && semCached is not null)
                {
                    _recorder.RecordAudit(null, "semantic-cache", 0, null, 0m, 0, sessionId, $"semantic-cache-hit (sim={semSim:F3})", true, null, false, ModelTier.Cheap);
                    _logger.LogInformation("Semantic Response Cache HIT! Similarity: {Sim:F3}, Matched Prompt: {Prompt}", semSim, matchedPrompt);
                    return semCached;
                }
            }
        }

        bool regeneratePenaltyApplied = false;

        PiiMap? piiMap = null;
        if (options.Routing.EnablePiiAnonymization)
        {
            var anonymized = PiiAnonymizer.AnonymizeRequest(request);
            request = anonymized.SanitizedRequest;
            piiMap = anonymized.PiiMap;
        }

        // 构造请求内容摘要（用于 dashboard 展示），取最后一条非空 user 消息文本，截断到 500 字符
        string? requestContent = null;
        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg.Role == "user")
            {
                var text = msg.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    requestContent = text.Length > 500 ? text.Substring(0, 500) + "..." : text;
                    break;
                }
            }
        }

        if (options.Routing.EnablePersonaDriftProtection && !string.IsNullOrEmpty(sessionId))
        {
            request = PersonaDriftGuard.ApplyPersonaAnchor(request);
        }

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
                    throw new BudgetExhaustedException(decision.Reason);
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, decision.Reason);
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
                    return ProcessResponse(fusionResult.Response, piiMap);
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
                    return ProcessResponse(fusionResult.Response, piiMap);
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

                    decimal cost = response.Usage is not null
                        ? CostCalculator.Compute(response.Usage, candidate)
                        : 0m;
                    // 质量因子：从非流式响应检测低质量信号（截断/空答/JSON 契约违约），乘性折减延迟 reward。
                    // 流式路径未累积 content，不接入（qualityFactor 默认 1.0）。
                    double qualityFactor = OutcomeRecorder.ExtractQualityFactor(response, options.Routing.QualityPenaltyFactor, request);
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds, decision, cost,
                        actualTier: candidate.Tier, qualityFactor: qualityFactor, completionTokens: response.Usage?.CompletionTokens ?? 0);
                    outcomeReported = true;
                    _recorder.RecordAffinity(sessionId, candidate.Name);
                    _recorder.RecordPromptCacheAffinity(request, candidate.Name);

                    if (response.Usage is not null)
                    {
                        _recorder.RecordCost(cost, sessionId);
                        _recorder.RecordAudit(null, candidate.Name, estimatedTokens, response.Usage, cost, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, false, routedTier,
                            timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
                            timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                    }
                    _recorder.RecordQuota(candidate.Name, response.Metadata);
                    _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);

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
                    if (options.Routing.EnableSemanticCache && !request.Stream)
                    {
                        string? promptText = GetLastUserPrompt(request);
                        if (!string.IsNullOrWhiteSpace(promptText))
                        {
                            await _semanticCache.StoreAsync(
                                promptText, finalResponse, TimeSpan.FromMinutes(options.Routing.SemanticCacheTtlMinutes), ct).ConfigureAwait(false);
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
                        routedTier, quotaLimited: true, requestContent: requestContent);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: false);
                    _logger.LogWarning("Model {Name} quota exhausted (status {Status}), trying next candidate",
                        candidate.Name, 429);
                }
                catch (ModelClientException ex) when (IsRetryable(ex))
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
                        $"upstream-status-{(int)ex.StatusCode}", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "network-error", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
                    _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, isGlobalTimeout ? "global-failover-timeout" : "timeout", false, routedTier, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
        string? feedbackKey = options.Routing.EnableRegenerateFeedback
            ? ResponseCacheKey.Compute(request)
            : null;
        bool regeneratePenaltyApplied = false;

        PiiMap? piiMap = null;
        if (options.Routing.EnablePiiAnonymization)
        {
            var anonymized = PiiAnonymizer.AnonymizeRequest(request);
            request = anonymized.SanitizedRequest;
            piiMap = anonymized.PiiMap;
        }

        // 构造请求内容摘要（用于 dashboard 展示），取最后一条非空 user 消息文本，截断到 500 字符
        string? requestContent = null;
        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg.Role == "user")
            {
                var text = msg.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    requestContent = text.Length > 500 ? text.Substring(0, 500) + "..." : text;
                    break;
                }
            }
        }

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
                    throw new BudgetExhaustedException(decision.Reason);
                throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, decision.Reason);
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
                    yield return RestorePii(line, piiMap);
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
                    catch (ModelClientException ex) when (IsRetryable(ex))
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
                        bool tripped = false;
                        double? preStreamReward = null;
                        if (quotaLimited)
                        {
                            var quotaError = (ModelClientException)preStreamFailure;
                            _recorder.RecordQuota(candidate.Name, quotaError.Metadata, rateLimited: true);
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
                            reward: preStreamReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                        _logger.LogWarning("Streaming model {Name} failed pre-stream ({Failure}), trying next{Tripped}",
                            candidate.Name, failure, tripped ? " (circuit tripped)" : "");

                        if (isGlobalTimeout)
                        {
                            throw new AllCandidatesFailedException(attemptedModels, lastModelName, lastStatusCode, lastErrorMessage, $"Global failover timeout ({options.Routing.FailoverGlobalTimeoutSeconds}s) exceeded.");
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

                            var restored = ProcessCompliance(RestorePii(line, piiMap), complianceBuffer, options.Routing);
                            totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(restored.Data ?? "");
                            if (totalBytesTransferred > maxResponseBytes)
                            {
                                throw new ResponseSizeLimitExceededException(maxResponseBytes,
                                    $"Response size limit exceeded ({maxResponseBytes} bytes).");
                            }
                            yield return restored;
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
                    // 流式未累积完整 content/finish_reason，质量因子不接入（默认 1.0）；仅传 actualTier 启用 per-tier 目标。
                    double reward = _recorder.RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds, decision, cost,
                        actualTier: candidate.Tier, completionTokens: finalUsage?.CompletionTokens ?? 0, timeToFirstTokenMs: firstLine.Metadata?.TimeToFirstTokenMs);
                    _recorder.RecordAffinity(sessionId, candidate.Name);
                    _recorder.RecordPromptCacheAffinity(request, candidate.Name);
                    _regenerateTracker.Record(feedbackKey, candidate.Name, success: true);
                    probeResolved = true;
                        attemptSw.Stop();
                    _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, finalUsage,
                        cost,
                        attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, true, routedTier,
                        isEstimated: isEstimated,
                        timeToFirstTokenMs: firstLine.Metadata?.TimeToFirstTokenMs,
                        reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
                                reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
    /// 可重试的失败状态码。429 不在此列：流式与非流式路径都在 <c>ModelClientException</c>
    /// 独立 catch 分支（<c>StatusCode == TooManyRequests</c>）先行捕获，走配额/健康隔离路径，
    /// 不到达这里。本方法只处理真正的可重试上游故障（408 / 5xx）。
    /// </summary>
    private static bool IsRetryable(ModelClientException exception)
    {
        int statusCode = (int)exception.StatusCode;
        return statusCode is 408 or >= 500 and <= 599;
    }

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
}
