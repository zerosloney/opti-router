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

            // 并行首试（Fusion-lite）：首轮 + 非流式 + 启用 + ≥2 候选时，并行尝试前 N 个，取最快成功。
            // 失败/取消全进入 failedInThisRequest，continue 后由串行降级链兜底。
            // 仅首轮触发（failedInThisRequest.Count==0）——已降级场景候选变少，并行收益低。
            if (failoverEnabled && options.Routing.EnableFusionMode
                && failedInThisRequest.Count == 0 && !request.Stream
                && decision.Candidates.Count >= 2)
            {
                var fusionResult = await TryParallelFirstAttemptAsync(
                    request, options, decision, estimatedTokens, routedTier,
                    sessionId, failedInThisRequest, attemptedModels, ct).ConfigureAwait(false);

                lastModelName = fusionResult.LastModelName;
                lastStatusCode = fusionResult.LastStatusCode;
                lastErrorMessage = fusionResult.LastErrorMessage;

                if (fusionResult.Response is not null)
                    return fusionResult.Response;
                // 全部失败/取消：failedInThisRequest 已填充，continue 到下一轮串行降级。
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
        string? upgradedFrom = null,
        bool isAdopted = true,
        string? parallelGroupId = null)
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
                ParallelGroupId: parallelGroupId));
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

        var verifySw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cheapClient = _clientProvider.GetClient(cheapModel);
            var verifyResponse = await cheapClient.CompleteAsync(verifyRequest, ct).ConfigureAwait(false);
            verifySw.Stop();

            bool confident = ResponseConfidenceChecker.IsConfident(verifyResponse);
            // 复核调用真实消耗 token，必须入账成本账本，否则开级联时预算系统性偏低（漂移）。
            decimal verifyCost = verifyResponse.Usage is not null
                ? CostCalculator.Compute(verifyResponse.Usage, cheapModel)
                : 0m;
            if (verifyResponse.Usage is not null)
                _ledger.Record(verifyCost, sessionId);

            RecordAudit(null, cheapModel.Name, estimatedTokens, verifyResponse.Usage, verifyCost, verifySw.ElapsedMilliseconds, sessionId,
                decision.Reason + "; cascade: self-verify " + (confident ? "confident" : "uncertain"),
                true, null, false, routedTier, cascadeTriggered: true);

            if (confident) return null;

            // 低置信 → 升级到首个可用 Strong 模型。
            // 升级目标从全量启用模型选，不依赖 decision.Candidates——
            // 候选链经 RuleClassifier/SemanticRouter 的 FilterByTier 砍成单 tier 后不含 Strong。
            // 排序与 RouterEngine 初始候选一致（Strong 优先 + MaxContextTokens 降序），结果可预测。
            var upgradeTarget = _options.CurrentValue.Models
                .Where(m => m.Enabled && m.Tier == ModelTier.Strong && !failedInThisRequest.Contains(m.Name))
                .OrderByDescending(m => m.MaxContextTokens)
                .FirstOrDefault();
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

    /// <summary>
    /// 并行首试（Fusion-lite）：对候选链前 N 个模型并行发起非流式请求，取最快成功响应，取消其余。
    /// </summary>
    /// <remarks>
    /// 成本语义：所有并行尝试的真实消耗都入账（上游对已发出的请求仍计费，否则预算系统性偏低）。
    /// 审计语义：每个尝试记一条，共享同一次调用的并行组 ID，仅采纳者 IsAdopted=true。
    /// 断路器语义：每个尝试独立占探测槽位；成功 RecordSuccess，真实失败 RecordFailure，被取消 ReleaseProbe。
    /// 全部失败/取消时把真实失败的模型加入 <paramref name="failedInThisRequest"/>，返回 Response=null 让调用方走串行降级链。
    /// </remarks>
    /// <returns>采纳的响应 + last* 三元组（供调用方回写局部变量）；Response 为 null 表示全部失败/取消。</returns>
    private async Task<FusionAttemptResult> TryParallelFirstAttemptAsync(
        ChatRequest request,
        RouterOptions options,
        RouterDecision decision,
        int estimatedTokens,
        ModelTier routedTier,
        string? sessionId,
        HashSet<string> failedInThisRequest,
        List<string> attemptedModels,
        CancellationToken ct)
    {
        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;
        int halfOpenMaxProbes = options.Routing.FailoverHalfOpenMaxProbes;
        int requiredSuccesses = options.Routing.FailoverHalfOpenRequiredSuccesses;
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;
        int maxParallel = options.Routing.FusionMaxParallel;

        // 1. 选参与并行的候选：前 N 个中能拿到探测许可的（闭合直接放行；半开占槽位；打开/槽位满跳过）。
        //    拿到许可的候选必须最终上报结果（成功/失败/释放），否则槽位泄漏。
        string groupId = Guid.NewGuid().ToString("N");
        var admitted = new List<(ModelEndpointOptions Model, bool WasHalfOpenProbe)>();
        var skipped = new List<ModelEndpointOptions>();

        foreach (var candidate in decision.Candidates.Take(maxParallel))
        {
            if (!_healthTracker.TryBeginProbe(candidate.Name, halfOpenMaxProbes))
            {
                skipped.Add(candidate);
                continue;
            }
            // 半开态才占槽位（闭合态 TryBeginProbe 返回 true 但不计 ActiveProbes，RecordSuccess/Failure 对闭合态是 no-op 探测释放）。
            bool isHalfOpen = _healthTracker.GetState(candidate.Name) == CircuitState.HalfOpen;
            admitted.Add((candidate, isHalfOpen));
            attemptedModels.Add(candidate.Name);
        }

        if (admitted.Count < 2)
        {
            // 并行凑不齐 2 个（多数模型在熔断/半开槽位满）：回退串行。
            // 已拿许可的候选需释放，让串行路径重新获取（避免重复占用）。
            foreach (var (m, _) in admitted)
                _healthTracker.ReleaseProbe(m.Name);
            // 把 attemptedModels 中刚加的并行候选移除（串行路径会重新加）。
            foreach (var (m, _) in admitted)
                attemptedModels.Remove(m.Name);
            return new FusionAttemptResult(null, null, null, null);
        }

        // 2. 并行发起。每个候选配 linked CTS，首个成功后 cancel 其余。
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var perCandidateCts = new List<(ModelEndpointOptions Model, CancellationTokenSource Cts, bool WasHalfOpen)>();
        var tasks = new List<Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs, bool WasHalfOpen)>>();

        foreach (var (model, wasHalfOpen) in admitted)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
            perCandidateCts.Add((model, linkedCts, wasHalfOpen));
            var modelCopy = model; // 闭包捕获。
            var wasHalfOpenCopy = wasHalfOpen;
            tasks.Add(Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = _clientProvider.GetClient(modelCopy);
                    var response = await client.CompleteRawAsync(request, linkedCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    return (modelCopy, response, (Exception?)null, sw.ElapsedMilliseconds, wasHalfOpenCopy);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return (modelCopy, (RawChatResponse?)null, ex, sw.ElapsedMilliseconds, wasHalfOpenCopy);
                }
            }, linkedCts.Token));
        }

        // 3. WhenAny 循环：首个成功立即采纳并 cancel 其余。
        RawChatResponse? adopted = null;
        string? adoptedModel = null;
        // remaining 保留 tasks 的完整元组泛型，避免 WhenAny 退化成 Task 丢失字段。
        var remaining = tasks.ToList();

        while (remaining.Count > 0)
        {
            var done = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(done);

            var (model, response, error, elapsedMs, wasHalfOpen) = await done.ConfigureAwait(false);

            // 成功路径：采纳，cancel 其余。
            if (response is not null && error is null)
            {
                // 成本入账（即使后续被取消的也在它们各自的 task 里处理——此处只记采纳者）。
                ChatUsage? usage = response.Usage;
                decimal cost = usage is not null ? CostCalculator.Compute(usage, model) : 0m;
                if (usage is not null)
                    _ledger.Record(cost, sessionId);

                _healthTracker.RecordSuccess(model.Name, requiredSuccesses);
                RecordAffinity(sessionId, model.Name);
                RecordAudit(null, model.Name, estimatedTokens, usage, cost, elapsedMs, sessionId,
                    decision.Reason + "; fusion: adopted", true, null, false, routedTier,
                    isAdopted: true, parallelGroupId: groupId);

                adopted = response;
                adoptedModel = model.Name;

                // 取消其余并行任务——它们的失败按"被取消"处理（ReleaseProbe），不记断路器失败。
                raceCts.Cancel();
                break;
            }

            // 失败路径：区分真实失败 vs 被取消（因别人成功）。
            bool cancelledByRace = raceCts.IsCancellationRequested
                && error is OperationCanceledException;

            if (cancelledByRace)
            {
                // 被取消（非自身失败）：仅释放探测槽位，不计断路器失败。
                _healthTracker.ReleaseProbe(model.Name);
                RecordAudit(null, model.Name, estimatedTokens, null, 0m, elapsedMs, sessionId,
                    decision.Reason + "; fusion: cancelled-by-race", false, "cancelled", false, routedTier,
                    isAdopted: false, parallelGroupId: groupId);
                continue;
            }

            // 真实失败：记断路器 + 审计，标记进入 failedInThisRequest（让串行降级排除它）。
            failedInThisRequest.Add(model.Name);
            lastModelName = model.Name;
            bool tripped = _healthTracker.RecordFailure(model.Name, threshold, cooldown);

            int status = error switch
            {
                ModelClientException mce => (int)mce.StatusCode,
                HttpRequestException => 503,
                OperationCanceledException => 408,
                _ => lastStatusCode ?? 500
            };
            lastStatusCode = status;
            lastErrorMessage = error?.Message ?? "unknown";

            RecordAudit(null, model.Name, estimatedTokens, null, 0m, elapsedMs, sessionId,
                decision.Reason + "; fusion: failed" + (tripped ? " (circuit tripped)" : ""),
                false, error?.Message, false, routedTier,
                isAdopted: false, parallelGroupId: groupId);
        }

        // 4. 等待所有被 cancel 的 task 收尾（避免 task 泄漏/未观察异常）。
        // 已被 raceCts.Cancel 的，WhenAll 会快速完成（抛 OperationCanceledException 被 task 内 try 吞）。
        try
        {
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
        catch
        {
            // task 内部已 try-catch，不应抛；防御性吞掉。
        }

        // 5. 清理被取消但 task 未走失败分支的候选探测槽位。
        // WhenAny 循环里 cancelledByRace 已处理；但 break 后 remaining 中可能还有未遍历的被 cancel task，
        // 它们返回 (model, null, OperationCanceledException)，上面 WhenAll 后没单独处理——这里补释放。
        // 已在循环内处理过的（cancelledByRace=true）不会重复：RecordSuccess/Failure/ReleaseProbe 对重复调用幂等或已置零。
        foreach (var (m, cts, wasHalfOpen) in perCandidateCts)
        {
            cts.Dispose();
            // 仅对未上报结果的候选释放（采纳者/已 RecordFailure 的跳过）。
            // 简化：对除采纳者和 failedInThisRequest 之外的，确保释放。
            // intentional-simple: ReleaseProbe 对 ActiveProbes=0 是 no-op，重复释放安全。
            if (m.Name != adoptedModel && !failedInThisRequest.Contains(m.Name))
                _healthTracker.ReleaseProbe(m.Name);
        }

        // 6. 被跳过的候选（熔断/槽位满）不影响本轮，留待串行降级处理（它们不在 attemptedModels，串行路径会尝试）。

        if (_logger.IsEnabled(LogLevel.Information))
        {
            if (adopted is not null)
            {
                _logger.LogInformation("Fusion parallel race: adopted {Model} (group {GroupId})", adoptedModel, groupId);
            }
            else
            {
                _logger.LogInformation("Fusion parallel race: all failed/cancelled (group {GroupId}), falling back to serial", groupId);
            }
        }

        // adopted 为 null（全部失败/取消）：failedInThisRequest 已含真实失败者，
        // 被取消者虽未计入 failedInThisRequest 但串行降级链会重试它们（除非它们也在 skipped）。
        // 若全部是 cancelled-by-race（无真实失败），说明采纳者实际存在——不可能走到这。
        return new FusionAttemptResult(adopted, lastModelName, lastStatusCode, lastErrorMessage);
    }

    /// <summary>并行首试结果：采纳的响应（可能为 null）+ 失败诊断三元组。</summary>
    private sealed record FusionAttemptResult(
        RawChatResponse? Response,
        string? LastModelName,
        int? LastStatusCode,
        string? LastErrorMessage);
}
