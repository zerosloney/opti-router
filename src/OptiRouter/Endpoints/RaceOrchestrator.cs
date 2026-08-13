using Microsoft.Extensions.Logging;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 并行首试（Fusion-lite）编排器：对候选链前 N 个模型并行发起非流式请求，取最快成功响应，取消其余。
/// </summary>
/// <remarks>
/// 成本语义：所有并行尝试的真实消耗都入账（上游对已发出的请求仍计费，否则预算系统性偏低）。
/// 审计语义：每个尝试记一条，共享同一次调用的并行组 ID，仅采纳者 IsAdopted=true。
/// 断路器语义：每个尝试独立占探测槽位；成功 RecordSuccess，真实失败 RecordFailure，被取消 ReleaseProbe。
/// 全部失败/取消时把真实失败的模型加入 failedInThisRequest，返回 Response=null 让调用方走串行降级链。
/// </remarks>
public sealed class RaceOrchestrator
{
    private readonly IModelClientProvider _clientProvider;
    private readonly ModelHealthTracker _healthTracker;
    private readonly OutcomeRecorder _recorder;
    private readonly ILogger<RaceOrchestrator> _logger;

    public RaceOrchestrator(
        IModelClientProvider clientProvider,
        ModelHealthTracker healthTracker,
        OutcomeRecorder recorder,
        ILogger<RaceOrchestrator> logger)
    {
        _clientProvider = clientProvider;
        _healthTracker = healthTracker;
        _recorder = recorder;
        _logger = logger;
    }

    /// <returns>采纳的响应 + last* 三元组（供调用方回写局部变量）；Response 为 null 表示全部失败/取消。</returns>
    public async Task<FusionAttemptResult> ExecuteAsync(
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
        var perCandidateCts = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new List<Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs, bool WasHalfOpen)>>();

        foreach (var (model, wasHalfOpen) in admitted)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
            perCandidateCts[model.Name] = linkedCts;
            tasks.Add(Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = _clientProvider.GetClient(model);
                    var response = await client.CompleteRawAsync(request, linkedCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    return (model, response, (Exception?)null, sw.ElapsedMilliseconds, wasHalfOpen);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return (model, (RawChatResponse?)null, ex, sw.ElapsedMilliseconds, wasHalfOpen);
                }
            }, linkedCts.Token));
        }

        // 3. WhenAny 循环：首个成功立即采纳并 cancel 其余。
        RawChatResponse? adopted = null;
        string? adoptedModel = null;
        // remaining 保留 tasks 的完整元组泛型，避免 WhenAny 退化成 Task 丢失字段。
        var remaining = tasks.ToList();
        // 循环内已记审计的候选（成功/失败/被取消），步骤 5 不重复处理。
        var accounted = new HashSet<string>(StringComparer.Ordinal);

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
                    _recorder.RecordCost(cost, sessionId);

                _recorder.RecordQuota(model.Name, response.Metadata);
                _healthTracker.RecordSuccess(model.Name, requiredSuccesses);
                _recorder.RecordThompsonOutcome(model.Name, elapsedMs, decision);
                _recorder.RecordAffinity(sessionId, model.Name, AffinitySignal.Weak);
                _recorder.RecordPromptCacheAffinity(request, model.Name);
                _recorder.RecordAudit(null, model.Name, estimatedTokens, usage, cost, elapsedMs, sessionId,
                    decision.Reason + "; fusion: adopted", true, null, false, routedTier,
                    isAdopted: true, parallelGroupId: groupId,
                    timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs);
                accounted.Add(model.Name);

                adopted = response;
                adoptedModel = model.Name;

                // 取消其余并行任务——它们的失败按"被取消"处理（ReleaseProbe），不记断路器失败。
                raceCts.Cancel();
                break;
            }

            // 失败路径：区分真实失败 vs 被取消（因别人成功）。
            // linkedCts.IsCancellationRequested 区分 race-cancel（raceCts 取消传播到 linkedCts）
            // 与 client 自身超时（HttpClient 取消内部 token，不取消 linkedCts）。
            // 后者应计入断路器失败，否则故障模型持续收流量。
            // linkedCts 与 admitted 一一对应，理论上必存在；防御找不到时按非取消处理。
            var linkedCts = perCandidateCts.TryGetValue(model.Name, out var ctsForModel) ? ctsForModel : null;
            bool cancelledByRace = raceCts.IsCancellationRequested
                && error is OperationCanceledException
                && linkedCts is not null
                && linkedCts.IsCancellationRequested;

            if (cancelledByRace)
            {
                // 被取消（非自身失败）：仅释放探测槽位，不计断路器失败。
                _healthTracker.ReleaseProbe(model.Name);
                // Thompson：竞速失败（另一模型已胜出，本模型被取消）——模型仍在途、未必坏，计部分奖励而非硬失败。
                _recorder.RecordThompsonRaceCancelled(model.Name, decision);
                // 预估成本入账：请求已发出到上游，上游对已接收的请求计费，但本地拿不到 Usage（响应未完整返回）。
                // 按 EstimatedInputTokens × input 价格估算，标注 IsEstimated=true 以区分真实成本。
                decimal estCost = OutcomeRecorder.EstimateInputCost(model, estimatedTokens);
                if (estCost > 0m)
                    _recorder.RecordCost(estCost, sessionId);
                _recorder.RecordAudit(null, model.Name, estimatedTokens, null, estCost, elapsedMs, sessionId,
                    decision.Reason + "; fusion: cancelled-by-race", false, "cancelled", false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: estCost > 0m);
                accounted.Add(model.Name);
                continue;
            }

            // 真实失败：记断路器 + 审计，标记进入 failedInThisRequest（让串行降级排除它）。
            failedInThisRequest.Add(model.Name);
            lastModelName = model.Name;
            bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(error);
            bool tripped = false;
            if (quotaLimited)
            {
                var quotaError = (ModelClientException)error!;
                _recorder.RecordQuota(model.Name, quotaError.Metadata, rateLimited: true);
                _healthTracker.ReleaseProbe(model.Name);
            }
            else
            {
                tripped = _healthTracker.RecordFailure(model.Name, threshold, cooldown);
                _recorder.RecordThompsonOutcome(model.Name, null, decision);
            }

            int status = error switch
            {
                ModelClientException mce => (int)mce.StatusCode,
                HttpRequestException => 503,
                OperationCanceledException => 408,
                _ => lastStatusCode ?? 500
            };
            lastStatusCode = status;
            lastErrorMessage = UpstreamFailureClassifier.SafeMessage(error, quotaLimited);

            // 真实失败同样预估入账：请求已到上游，上游按已处理 input 计费。
            decimal failedEstCost = quotaLimited ? 0m : OutcomeRecorder.EstimateInputCost(model, estimatedTokens);
            if (failedEstCost > 0m)
                _recorder.RecordCost(failedEstCost, sessionId);
            _recorder.RecordAudit(null, model.Name, estimatedTokens, null, failedEstCost, elapsedMs, sessionId,
                decision.Reason + "; fusion: failed" + (tripped ? " (circuit tripped)" : ""),
                false, UpstreamFailureClassifier.SafeMessage(error, quotaLimited), false, routedTier,
                isAdopted: false, parallelGroupId: groupId, isEstimated: failedEstCost > 0m,
                quotaLimited: quotaLimited);
            accounted.Add(model.Name);
        }

        // 4. 等待所有被 cancel 的 task 收尾（避免 task 泄漏/未观察异常）。
        //    已被 raceCts.Cancel 的，WhenAll 会快速完成（抛 OperationCanceledException 被 task 内 try 吞）。
        try
        {
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
        catch
        {
            // task 内部已 try-catch，不应抛；防御性吞掉。
        }

        // 释放所有候选的 linked CTS。
        foreach (var cts in perCandidateCts.Values)
            cts.Dispose();

        // 5. 清理 break 后未遍历的候选，按 task 实际结果区分"成功"vs"取消"，避免误算。
        //    WhenWhenAll 已使任务全部完成，此处再 await 立即返回缓存结果。
        foreach (var task in remaining)
        {
            var (m, response, error, elapsedMs, _) = await task.ConfigureAwait(false);

            // 跳过循环内已记审计的（成功采纳/失败/被取消）。
            if (accounted.Contains(m.Name))
                continue;

            // 竞态窗口内该候选在 cancel 传播前已收到成功响应：计真实成功 + 真实成本。
            // 否则模型熔断器收不到成功信号（可能误开路），且成本被低估为估算值。
            if (response is not null && error is null)
            {
                ChatUsage? usage = response.Usage;
                decimal cost = usage is not null ? CostCalculator.Compute(usage, m) : 0m;
                if (usage is not null)
                    _recorder.RecordCost(cost, sessionId);
                _recorder.RecordQuota(m.Name, response.Metadata);
                _healthTracker.RecordSuccess(m.Name, requiredSuccesses);
                _recorder.RecordThompsonOutcome(m.Name, elapsedMs, decision);
                _recorder.RecordAudit(null, m.Name, estimatedTokens, usage, cost, elapsedMs, sessionId,
                    decision.Reason + "; fusion: adopted (post-break)", true, null, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId,
                    timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs);
                accounted.Add(m.Name);
                continue;
            }

            // 真正被取消：释放探测槽位 + 预估成本入账。
            // 前提假设：请求已发出到上游，上游按已处理的 input 计费（本地拿不到 Usage）。
            // 该估算标 IsEstimated=true，区分于真实成本；若在 cancel 传播前请求未发出，此项可能高估。
            bool postBreakQuotaLimited = UpstreamFailureClassifier.IsQuotaLimited(error);
            _healthTracker.ReleaseProbe(m.Name);
            if (postBreakQuotaLimited)
            {
                var quotaError = (ModelClientException)error!;
                _recorder.RecordQuota(m.Name, quotaError.Metadata, rateLimited: true);
            }
            else
            {
                _recorder.RecordThompsonRaceCancelled(m.Name, decision);
            }
            decimal estCost = postBreakQuotaLimited ? 0m : OutcomeRecorder.EstimateInputCost(m, estimatedTokens);
            if (estCost > 0m)
                _recorder.RecordCost(estCost, sessionId);
            _recorder.RecordAudit(null, m.Name, estimatedTokens, null, estCost, elapsedMs, sessionId,
                decision.Reason + "; fusion: cancelled-by-race (post-break)", false,
                postBreakQuotaLimited ? "quota-exhausted" : "cancelled", false, routedTier,
                isAdopted: false, parallelGroupId: groupId, isEstimated: estCost > 0m,
                quotaLimited: postBreakQuotaLimited);
            accounted.Add(m.Name);
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

}
