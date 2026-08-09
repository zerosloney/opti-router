using Microsoft.Extensions.Logging;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 融合路由（OpenRouter Fusion 式 quality router）：
/// 并行 panel 作答 → analyst 结构化分析 → outer 写最终答案。
/// 仅非流式首轮触发。失败时返回 Response=null，由调用方继续 Fusion-lite 或串行降级链。
/// </summary>
/// <remarks>
/// 成本语义：N 个 panel + 1 analyst + 1 outer 全部按真实/预估成本入账。
/// 审计语义：每条调用记一条记录，共享 <c>ParallelGroupId</c>，<c>FusionRole</c> 区分 panel/analyst/outer。
/// 断路器语义：panel 各占探测槽位后用 RecordSuccess/ReleaseProbe 结算；analyst/outer 直接调用不占槽位（非候选链探活）。
/// </remarks>
public sealed class FusionRouter
{
    private readonly IModelClientProvider _clientProvider;
    private readonly ModelHealthTracker _healthTracker;
    private readonly OutcomeRecorder _recorder;
    private readonly ILogger<FusionRouter> _logger;

    public FusionRouter(
        IModelClientProvider clientProvider,
        ModelHealthTracker healthTracker,
        OutcomeRecorder recorder,
        ILogger<FusionRouter> logger)
    {
        _clientProvider = clientProvider;
        _healthTracker = healthTracker;
        _recorder = recorder;
        _logger = logger;
    }

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
        var routing = options.Routing;
        int panelSize = routing.FusionRouterPanelSize;
        int halfOpenMaxProbes = routing.FailoverHalfOpenMaxProbes;
        int requiredSuccesses = routing.FailoverHalfOpenRequiredSuccesses;
        int threshold = routing.FailoverFailureThreshold;
        int cooldown = routing.FailoverCooldownSeconds;
        string groupId = Guid.NewGuid().ToString("N");

        // 1. 选 panel 候选：前 N 个能拿到探测许可的。半开态占槽位，闭合直接放行。
        var admitted = new List<ModelEndpointOptions>();
        foreach (var candidate in decision.Candidates.Take(panelSize))
        {
            if (!_healthTracker.TryBeginProbe(candidate.Name, halfOpenMaxProbes))
                continue;
            admitted.Add(candidate);
            attemptedModels.Add(candidate.Name);
        }

        if (admitted.Count < 2)
        {
            foreach (var m in admitted)
                _healthTracker.ReleaseProbe(m.Name);
            foreach (var m in admitted)
                attemptedModels.Remove(m.Name);
            return new FusionAttemptResult(null, null, null, null);
        }

        // 2. 并行 fire panel，收集全部回答。
        ChatRequest panelRequest = request with
        {
            Temperature = request.Temperature ?? routing.FusionRouterTemperature
        };
        var panelResults = new List<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)>();
        var panelTasks = new List<Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)>>();

        foreach (var model in admitted)
        {
            var modelCopy = model;
            panelTasks.Add(InvokePanelAsync(modelCopy));

            async Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)> InvokePanelAsync(
                ModelEndpointOptions panelModel)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = _clientProvider.GetClient(panelModel);
                    var resp = await client.CompleteRawAsync(panelRequest, ct).ConfigureAwait(false);
                    sw.Stop();
                    return (panelModel, resp, (Exception?)null, sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    sw.Stop();
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return (panelModel, (RawChatResponse?)null, ex, sw.ElapsedMilliseconds);
                }
            }
        }

        // 3. 等待全部 panel 完成，逐条处理。
        try
        {
            await Task.WhenAll(panelTasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端取消不是模型健康信号：释放所有可能占用的半开探测槽，且不记
            // failure、Thompson 惩罚、预估成本或失败审计。
            foreach (var model in admitted)
                _healthTracker.ReleaseProbe(model.Name);
            throw;
        }

        foreach (var task in panelTasks)
        {
            // 各 task 内部吞异常并返回包含 Model 的元组，await 正常返回。
            var result = await task.ConfigureAwait(false);
            panelResults.Add(result);
        }

        // 4. 处理每个 panel 结果：提取文本、记账、审计、健康跟踪。
        var panelAnswers = new List<(string Model, string Text)>();
        string? lastModelName = null;
        int? lastStatusCode = null;
        string? lastErrorMessage = null;

        foreach (var (model, response, error, elapsedMs) in panelResults)
        {
            if (response is not null && error is null)
            {
                // 成功
                string text = ResponseConfidenceChecker.ExtractAssistantText(response);
                panelAnswers.Add((model.Name, text));

                ChatUsage? usage = response.Usage;
                decimal cost = usage is not null ? CostCalculator.Compute(usage, model) : 0m;
                if (usage is not null)
                    _recorder.RecordCost(cost, sessionId);

                _healthTracker.RecordSuccess(model.Name, requiredSuccesses);
                _recorder.RecordThompsonOutcome(model.Name, elapsedMs < routing.ThompsonLatencyTargetMs);
                _recorder.RecordAudit(null, model.Name, estimatedTokens, usage, cost, elapsedMs, sessionId,
                    decision.Reason + "; fusion-router: panel success", true, null, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: false, fusionRole: "panel");
            }
            else
            {
                // 失败
                failedInThisRequest.Add(model.Name);
                lastModelName = model.Name;
                var tripped = _healthTracker.RecordFailure(model.Name, threshold, cooldown);
                _recorder.RecordThompsonOutcome(model.Name, false);

                int status = error switch
                {
                    ModelClientException mce => (int)mce.StatusCode,
                    HttpRequestException => 503,
                    OperationCanceledException => 408,
                    _ => 500
                };
                lastStatusCode = status;
                lastErrorMessage = error?.Message ?? "unknown";

                decimal estCost = OutcomeRecorder.EstimateInputCost(model, estimatedTokens);
                if (estCost > 0m)
                    _recorder.RecordCost(estCost, sessionId);
                _recorder.RecordAudit(null, model.Name, estimatedTokens, null, estCost, elapsedMs, sessionId,
                    decision.Reason + "; fusion-router: panel failed" + (tripped ? " (circuit tripped)" : ""),
                    false, error?.Message, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: estCost > 0m, fusionRole: "panel");
            }
        }

        // 5. 若所有 panel 全部失败，回退串行。
        if (panelAnswers.Count == 0)
        {
            _logger.LogInformation("Fusion router: all panel models failed (group {GroupId}), falling back to serial", groupId);
            // 注意：已记入 failedInThisRequest 的 panel 失败模型会在串行降级中被排除。
            // 被跳过的候选（未 admission）仍在候选链中，串行会尝试它们。
            return new FusionAttemptResult(null, lastModelName, lastStatusCode, lastErrorMessage);
        }

        // 6. 解析 analyst 模型。
        ModelEndpointOptions? analystModel = null;
        if (!string.IsNullOrWhiteSpace(routing.FusionRouterAnalystModel))
        {
            analystModel = options.Models
                .FirstOrDefault(m => m.Enabled && m.Name.Equals(routing.FusionRouterAnalystModel, StringComparison.OrdinalIgnoreCase));
        }
        analystModel ??= decision.Candidates[0];

        // 7. 调用 analyst（结构化分析）。
        string analystPrompt = string.IsNullOrWhiteSpace(routing.FusionRouterAnalystPrompt)
            ? FusionSynthesis.DefaultAnalystPrompt
            : routing.FusionRouterAnalystPrompt;

        var analystRequest = FusionSynthesis.BuildAnalystRequest(request, panelAnswers, analystPrompt, routing.FusionRouterTemperature);
        FusionAnalysis? analysis = null;
        long analystElapsedMs = 0;

        try
        {
            var analystSw = System.Diagnostics.Stopwatch.StartNew();
            var analystClient = _clientProvider.GetClient(analystModel);
            var analystResponse = await analystClient.CompleteRawAsync(analystRequest, ct).ConfigureAwait(false);
            analystSw.Stop();
            analystElapsedMs = analystSw.ElapsedMilliseconds;

            ChatUsage? analystUsage = analystResponse.Usage;
            decimal analystCost = analystUsage is not null ? CostCalculator.Compute(analystUsage, analystModel) : 0m;
            if (analystUsage is not null)
                _recorder.RecordCost(analystCost, sessionId);

            _healthTracker.RecordSuccess(analystModel.Name, requiredSuccesses);
            _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, analystUsage, analystCost, analystElapsedMs, sessionId,
                decision.Reason + "; fusion-router: analyst", true, null, false, routedTier,
                isAdopted: false, parallelGroupId: groupId, isEstimated: false, fusionRole: "analyst");

            analysis = FusionSynthesis.ParseAnalysis(analystResponse);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Fusion router analyst call failed (model {Model}), falling back to serial", analystModel.Name);
            // analyst 失败不记 failedInThisRequest（analyst 不是候选模型），释放主力模型用串行。
            return new FusionAttemptResult(null, analystModel.Name, 502, ex.Message);
        }

        // 分析解析失败（JSON 解析/格式不对）→ 回退串行。
        if (analysis is null)
        {
            _logger.LogWarning("Fusion router analyst parse failed (model {Model}), falling back to serial", analystModel.Name);
            return new FusionAttemptResult(null, analystModel.Name, 502, "analyst parse failed");
        }

        // 8. 解析 outer 模型并调用。
        ModelEndpointOptions? outerModel = null;
        if (!string.IsNullOrWhiteSpace(routing.FusionRouterOuterModel))
        {
            outerModel = options.Models
                .FirstOrDefault(m => m.Enabled && m.Name.Equals(routing.FusionRouterOuterModel, StringComparison.OrdinalIgnoreCase));
        }
        outerModel ??= decision.Candidates[0];

        var outerRequest = FusionSynthesis.BuildOuterRequest(
            request, analysis, FusionSynthesis.DefaultOuterPrompt, routing.FusionRouterMaxOutputTokens);

        try
        {
            var outerSw = System.Diagnostics.Stopwatch.StartNew();
            var outerClient = _clientProvider.GetClient(outerModel);
            var outerResponse = await outerClient.CompleteRawAsync(outerRequest, ct).ConfigureAwait(false);
            outerSw.Stop();

            ChatUsage? outerUsage = outerResponse.Usage;
            decimal outerCost = outerUsage is not null ? CostCalculator.Compute(outerUsage, outerModel) : 0m;
            if (outerUsage is not null)
                _recorder.RecordCost(outerCost, sessionId);

            _healthTracker.RecordSuccess(outerModel.Name, requiredSuccesses);
            _recorder.RecordThompsonOutcome(outerModel.Name, outerSw.ElapsedMilliseconds < routing.ThompsonLatencyTargetMs);
            _recorder.RecordAffinity(sessionId, outerModel.Name);
            _recorder.RecordAudit(null, outerModel.Name, estimatedTokens, outerUsage, outerCost, outerSw.ElapsedMilliseconds, sessionId,
                decision.Reason + "; fusion-router: outer", true, null, false, routedTier,
                isAdopted: true, parallelGroupId: groupId, isEstimated: false, fusionRole: "outer");

            _logger.LogInformation("Fusion router: completed (group {GroupId}), panel={PanelCount}, analyst={Analyst}, outer={Outer}",
                groupId, panelAnswers.Count, analystModel.Name, outerModel.Name);

            return new FusionAttemptResult(outerResponse, outerModel.Name, null, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Fusion router outer call failed (model {Model}), falling back to serial", outerModel.Name);
            return new FusionAttemptResult(null, outerModel.Name, 502, ex.Message);
        }
    }
}
