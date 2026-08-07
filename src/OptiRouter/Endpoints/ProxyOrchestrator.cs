using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

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
    private readonly CostLedger _ledger;
    private readonly ModelHealthTracker _healthTracker;
    private readonly ILogger<ProxyOrchestrator> _logger;
    private readonly IRequestAuditStore _auditStore;
    private readonly IMemoryCache _affinityCache;
    private bool _disposed;

    /// <summary>
    /// 初始化编排器。
    /// </summary>
    /// <param name="clientProvider">模型客户端提供者。</param>
    /// <param name="engine">路由引擎。</param>
    /// <param name="options">路由配置监视器。Routing 开关经 reload 可生效；
    /// 但 Models 端点（BaseUrl/ApiKey/Timeout）按模型名缓存在 ModelClientProvider，变更需重启进程。</param>
    /// <param name="ledger">成本账本。</param>
    /// <param name="healthTracker">跨请求模型健康跟踪器。</param>
    /// <param name="auditStore">请求审计存储。</param>
    /// <param name="affinityCache">会话粘性缓存。请求成功后回写本次模型名，供 SessionAffinityPolicy 下次提升。</param>
    /// <param name="logger">日志记录器。</param>
    public ProxyOrchestrator(
        IModelClientProvider clientProvider,
        RouterEngine engine,
        IOptionsMonitor<RouterOptions> options,
        CostLedger ledger,
        ModelHealthTracker healthTracker,
        IRequestAuditStore auditStore,
        IMemoryCache affinityCache,
        ILogger<ProxyOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(healthTracker);
        ArgumentNullException.ThrowIfNull(auditStore);
        ArgumentNullException.ThrowIfNull(affinityCache);
        ArgumentNullException.ThrowIfNull(logger);

        _clientProvider = clientProvider;
        _engine = engine;
        _options = options;
        _ledger = ledger;
        _healthTracker = healthTracker;
        _auditStore = auditStore;
        _affinityCache = affinityCache;
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

        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;

        while (true)
        {
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
                try
                {
                    var client = _clientProvider.GetClient(candidate);
                    var response = await client.CompleteRawAsync(request, ct).ConfigureAwait(false);
                    attemptSw.Stop();

                    if (response.Usage is not null)
                    {
                        var cost = CostCalculator.Compute(response.Usage, candidate);
                        _ledger.Record(cost, sessionId);
                        RecordAudit(null, candidate.Name, estimatedTokens, response.Usage, cost, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, false, routedTier);
                    }
                    else
                    {
                        RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, false, routedTier);
                    }
                    _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);
                    outcomeReported = true;
                    RecordAffinity(sessionId, candidate.Name);

                    // 级联自校验：Cheap 首选 + 启用 + 采样命中 → 自校验，低置信升级 Strong。
                    // 仅非流式（流式首 chunk 已透传无法切模型）。失败不影响主流程，返回原 Cheap 答案。
                    if (candidate.Tier == ModelTier.Cheap)
                    {
                        var upgraded = await TryCascadeUpgradeAsync(
                            request, response, decision, candidate, estimatedTokens, routedTier, sessionId, failedInThisRequest, ct).ConfigureAwait(false);
                        if (upgraded is not null)
                            return upgraded;
                    }

                    _logger.LogInformation("Non-streaming request completed: model={Model}, cost={Cost}",
                        candidate.Name, response.Usage is not null
                            ? CostCalculator.Compute(response.Usage, candidate).ToString("F6")
                            : "unknown");

                    return response;
                }
                catch (ModelClientException ex) when (IsRetryable(ex))
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = (int)ex.StatusCode;
                    lastErrorMessage = ex.Message;
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    outcomeReported = true;
                    RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, ex.Message, false, routedTier);
                    _logger.LogWarning("Model {Name} failed (status {Status}), trying next candidate{Tripped}",
                        candidate.Name, ex.StatusCode, tripped ? " (circuit tripped)" : "");
                }
                catch (HttpRequestException ex)
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = 503;
                    lastErrorMessage = ex.Message;
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    outcomeReported = true;
                    RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, ex.Message, false, routedTier);
                    _logger.LogWarning(ex, "Model {Name} network request failed, trying next candidate{Tripped}",
                        candidate.Name, tripped ? " (circuit tripped)" : "");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    attemptSw.Stop();
                    lastModelName = candidate.Name;
                    lastStatusCode = 408;
                    lastErrorMessage = "Request timed out inside the proxy.";
                    // 客户端内部超时，非外部取消，记失败继续。
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    outcomeReported = true;
                    RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "timeout", false, routedTier);
                    _logger.LogWarning("Model {Name} timed out, trying next{Tripped}",
                        candidate.Name, tripped ? " (circuit tripped)" : "");
                }
                finally
                {
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

        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;
        long totalBytesTransferred = 0;
        long maxResponseBytes = options.Routing.MaxResponseStreamBytes;

        while (true)
        {
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
                        enumerator = client.StreamRawAsync(request, ct).GetAsyncEnumerator(ct);
                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            firstLine = enumerator.Current;
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
                    catch (ModelClientException ex) when (IsRetryable(ex))
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = (int)ex.StatusCode;
                        lastErrorMessage = ex.Message;
                    }
                    catch (HttpRequestException ex)
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = 503;
                        lastErrorMessage = ex.Message;
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        preStreamFailure = ex;
                        lastModelName = candidate.Name;
                        lastStatusCode = 408;
                        lastErrorMessage = "Request timed out inside the proxy.";
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
                        bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                        probeResolved = true;
                        string failure = preStreamFailure is ModelClientException modelFailure
                            ? $"status {(int)modelFailure.StatusCode}"
                            : preStreamFailure.Message;
                        RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                            attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, failure, true, routedTier);
                        _logger.LogWarning("Streaming model {Name} failed pre-stream ({Failure}), trying next{Tripped}",
                            candidate.Name, failure, tripped ? " (circuit tripped)" : "");
                        continue;
                    }

                    ArgumentNullException.ThrowIfNull(enumerator);

                    // Phase 2: 首行在 try-catch 之外 yield，避免 CS1626。
                    totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(firstLine.Data ?? "");
                    if (totalBytesTransferred > maxResponseBytes)
                    {
                        throw new InvalidOperationException($"Response size limit exceeded ({maxResponseBytes} bytes).");
                    }
                    yield return firstLine;

                    // 继续 yield 剩余行。内层只有 finally，无 catch，允许 yield。
                    try
                    {
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

                            totalBytesTransferred += System.Text.Encoding.UTF8.GetByteCount(line.Data ?? "");
                            if (totalBytesTransferred > maxResponseBytes)
                            {
                                throw new InvalidOperationException($"Response size limit exceeded ({maxResponseBytes} bytes).");
                            }
                            yield return line;
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }

                    // 流正常结束，记账 + 标记健康。
                    if (finalUsage is not null)
                    {
                        _ledger.Record(CostCalculator.Compute(finalUsage, candidate), sessionId);
                    }
                    _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);
                    RecordAffinity(sessionId, candidate.Name);
                    probeResolved = true;
                    attemptSw.Stop();
                    RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, finalUsage,
                        finalUsage is not null ? CostCalculator.Compute(finalUsage, candidate) : 0m,
                        attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, true, null, true, routedTier);
                    _logger.LogInformation("Streaming request completed: model={Model}, cost={Cost}",
                        candidate.Name, finalUsage is not null
                            ? CostCalculator.Compute(finalUsage, candidate).ToString("F6")
                            : "unknown");
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
                            RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                                attemptSw.ElapsedMilliseconds, sessionId, decision.Reason, false, "stream-faulted", true, routedTier);
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

    private static bool IsRetryable(ModelClientException exception)
    {
        int statusCode = (int)exception.StatusCode;
        return statusCode is 408 or 429 or >= 500 and <= 599;
    }

    private void RecordAudit(
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
        string? upgradedFrom = null)
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
                UpgradedFrom: upgradedFrom));
        }
        catch
        {
            // Audit recording must not break the request path.
        }
    }

    /// <summary>
    /// 记录会话粘性：成功命中某模型后写入内存缓存，供 <see cref="SessionAffinityPolicy"/> 下次决策提升该模型。
    /// 仅在启用会话粘性且存在 sessionId 时写。写失败（理论上 IMemoryCache 不会抛）不影响主流程。
    /// </summary>
    private void RecordAffinity(string? sessionId, string modelName)
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

    /// <summary>
    /// Cheap→Strong 级联自校验。返回 null 表示不升级（用原 Cheap 答案）；返回 RawChatResponse 表示升级到 Strong 的重答结果。
    /// 触发条件：EnableCascadeUpgrade 且采样命中（CascadeUpgradeSampleRate）。自校验用同 Cheap 模型，低置信则升级候选链首个 Strong。
    /// 级联全程异常吞掉返回 null：质量兜底不应破坏已成功的请求。
    /// </summary>
    private async Task<RawChatResponse?> TryCascadeUpgradeAsync(
        ChatRequest originalRequest,
        RawChatResponse cheapResponse,
        RouterDecision decision,
        ModelEndpointOptions cheapModel,
        int estimatedTokens,
        ModelTier routedTier,
        string? sessionId,
        HashSet<string> failedInThisRequest,
        CancellationToken ct)
    {
        var routing = _options.CurrentValue.Routing;
        if (!routing.EnableCascadeUpgrade) return null;

        double rate = routing.CascadeUpgradeSampleRate;
        if (rate <= 0 || Random.Shared.NextDouble() >= rate) return null;

        // 提取 Cheap 答案文本；为空（多模态/解析失败）则跳过，避免无效自校验。
        string cheapAnswer = ResponseConfidenceChecker.ExtractAssistantText(cheapResponse);
        if (string.IsNullOrWhiteSpace(cheapAnswer)) return null;

        string verifyPrompt = string.IsNullOrWhiteSpace(routing.CascadeUpgradeSelfVerifyPrompt)
            ? ResponseConfidenceChecker.DefaultSelfVerifyPrompt
            : routing.CascadeUpgradeSelfVerifyPrompt;

        var verifyRequest = ResponseConfidenceChecker.BuildVerificationRequest(originalRequest, cheapAnswer, verifyPrompt);

        try
        {
            var cheapClient = _clientProvider.GetClient(cheapModel);
            var verifyResponse = await cheapClient.CompleteAsync(verifyRequest, ct).ConfigureAwait(false);

            bool confident = ResponseConfidenceChecker.IsConfident(verifyResponse);
            // 记录自校验事件（不记 cost，复核调用暂不单独计费；通过 CascadeTriggered 标记可离线统计触发率）。
            RecordAudit(null, cheapModel.Name, estimatedTokens, verifyResponse.Usage, 0m, 0, sessionId,
                decision.Reason + "; cascade: self-verify " + (confident ? "confident" : "uncertain"),
                true, null, false, routedTier, cascadeTriggered: true);

            if (confident) return null;

            // 低置信 → 升级候选链首个 Strong 且未在本请求失败的模型。
            var upgradeTarget = decision.Candidates.FirstOrDefault(c =>
                c.Tier == ModelTier.Strong && !failedInThisRequest.Contains(c.Name));
            if (upgradeTarget is null) return null;

            var strongSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var strongClient = _clientProvider.GetClient(upgradeTarget);
                var strongResponse = await strongClient.CompleteRawAsync(originalRequest, ct).ConfigureAwait(false);
                strongSw.Stop();

                if (strongResponse.Usage is not null)
                {
                    var cost = CostCalculator.Compute(strongResponse.Usage, upgradeTarget);
                    _ledger.Record(cost, sessionId);
                }
                _healthTracker.RecordSuccess(upgradeTarget.Name,
                    routing.FailoverHalfOpenRequiredSuccesses);
                RecordAffinity(sessionId, upgradeTarget.Name);

                RecordAudit(null, upgradeTarget.Name, estimatedTokens, strongResponse.Usage,
                    strongResponse.Usage is not null ? CostCalculator.Compute(strongResponse.Usage, upgradeTarget) : 0m,
                    strongSw.ElapsedMilliseconds, sessionId, decision.Reason + "; cascade: upgraded from " + cheapModel.Name,
                    true, null, false, routedTier, cascadeTriggered: true, upgradedFrom: cheapModel.Name);

                _logger.LogInformation("Cascade upgrade: {Cheap} -> {Strong} (self-verify uncertain)",
                    cheapModel.Name, upgradeTarget.Name);

                return strongResponse;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
            {
                // 升级调用失败：记录但不抛，返回 null 让调用方用原 Cheap 答案（已有，质量兜底不优于崩溃）。
                _logger.LogWarning(ex, "Cascade upgrade to {Strong} failed, returning cheap answer", upgradeTarget.Name);
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            // 自校验本身失败：吞掉，用原 Cheap 答案。级联是优化路径，非主流程。
            _logger.LogDebug(ex, "Cascade self-verify failed for {Cheap}, skipping upgrade", cheapModel.Name);
            return null;
        }
    }
}
