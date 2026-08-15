using System.Text;
using System.Text.Json;
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
    private readonly FusionPanelSelector _panelSelector;
    private readonly UpstreamQuotaStateStore? _quotaStore;
    private readonly ILogger<FusionRouter> _logger;

    public FusionRouter(
        IModelClientProvider clientProvider,
        ModelHealthTracker healthTracker,
        OutcomeRecorder recorder,
        FusionPanelSelector panelSelector,
        ILogger<FusionRouter> logger,
        UpstreamQuotaStateStore? quotaStore = null)
    {
        _clientProvider = clientProvider;
        _healthTracker = healthTracker;
        _recorder = recorder;
        _panelSelector = panelSelector;
        _logger = logger;
        _quotaStore = quotaStore;
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
        var panelSelection = _panelSelector.Select(decision, routing, _quotaStore);
        int panelSize = panelSelection.RequestedSize;

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

        int halfOpenMaxProbes = routing.FailoverHalfOpenMaxProbes;
        int requiredSuccesses = routing.FailoverHalfOpenRequiredSuccesses;
        int threshold = routing.FailoverFailureThreshold;
        int cooldown = routing.FailoverCooldownSeconds;
        string groupId = Guid.NewGuid().ToString("N");

        // 1. 选 panel 候选：前 N 个能拿到探测许可的。半开态占槽位，闭合直接放行。
        var admitted = new List<ModelEndpointOptions>();
        foreach (var candidate in panelSelection.RankedCandidates)
        {
            if (admitted.Count >= panelSize) break;
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

        // 2. 并行 fire panel，收集全部回答。每个 panel 绑定独立超时 CTS（若配置）。
        // panel 专用温度（P1）：发散采样，优先 PanelTemperature，其次 FusionRouterTemperature。
        ChatRequest panelRequest = request with
        {
            Temperature = request.Temperature ?? routing.FusionRouterPanelTemperature ?? routing.FusionRouterTemperature
        };
        var panelResults = new List<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)>();
        var panelTasks = new List<Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)>>();
        var panelCtsList = new List<CancellationTokenSource>();
        int panelTimeoutMs = routing.FusionRouterPanelTimeoutSeconds * 1000;

        foreach (var model in admitted)
        {
            // 每 panel 一个独立 linked CTS：panel 超时只取消自己，不影响其他 panel。
            // 0 = 不启用 panel 级超时，linkedToken 即 ct 本身（CreateLinkedTokenSource 单 token 仍可安全 Dispose）。
            CancellationTokenSource panelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (panelTimeoutMs > 0)
                panelCts.CancelAfter(panelTimeoutMs);
            panelCtsList.Add(panelCts);
            panelTasks.Add(InvokePanelAsync(model, panelCts.Token));

            async Task<(ModelEndpointOptions Model, RawChatResponse? Response, Exception? Error, long ElapsedMs)> InvokePanelAsync(
                ModelEndpointOptions panelModel, CancellationToken panelToken)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = _clientProvider.GetClient(panelModel);
                    var resp = await client.CompleteRawAsync(panelRequest, panelToken).ConfigureAwait(false);
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

        // 3. 等待全部 panel 完成，逐条处理。panel 超时 CTS 统一在 finally 释放（CTS.Dispose 幂等）。
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
        finally
        {
            // WhenAll 返回（正常或外部取消）后，所有 panel task 已结束，panel 超时 CTS 不再被引用。
            foreach (var ctsItem in panelCtsList)
                ctsItem.Dispose();
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

                _recorder.RecordQuota(model.Name, response.Metadata);
                _healthTracker.RecordSuccess(model.Name, requiredSuccesses);
                double reward = _recorder.RecordThompsonOutcome(model.Name, elapsedMs, decision, completionTokens: usage?.CompletionTokens ?? 0);
                _recorder.RecordAudit(null, model.Name, estimatedTokens, usage, cost, elapsedMs, sessionId,
                    decision.Reason + "; fusion-router: panel success", true, null, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: false, fusionRole: "panel",
                    timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs,
                    reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
            }
            else
            {
                // 失败
                failedInThisRequest.Add(model.Name);
                lastModelName = model.Name;
                bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(error);
                bool tripped = false;
                double? failureReward = null;
                if (quotaLimited)
                {
                    var quotaError = (ModelClientException)error!;
                    _recorder.RecordQuota(model.Name, quotaError.Metadata, rateLimited: true);
                    _healthTracker.ReleaseProbe(model.Name);
                }
                else
                {
                    tripped = _healthTracker.RecordFailure(model.Name, threshold, cooldown);
                    failureReward = _recorder.RecordThompsonOutcome(model.Name, null, decision);
                }

                int status = error switch
                {
                    ModelClientException mce => (int)mce.StatusCode,
                    HttpRequestException => 503,
                    OperationCanceledException => 408,
                    _ => 500
                };
                lastStatusCode = status;
                // panel 超时（panelTimeoutMs>0 且非外部 ct 取消）→ status 408，reason 显式标注便于审计区分。
                bool panelTimedOut = error is OperationCanceledException && !ct.IsCancellationRequested;
                lastErrorMessage = panelTimedOut ? "panel-timeout" : UpstreamFailureClassifier.SafeMessage(error, quotaLimited);

                decimal estCost = quotaLimited ? 0m : OutcomeRecorder.EstimateInputCost(model, estimatedTokens);
                if (estCost > 0m)
                    _recorder.RecordCost(estCost, sessionId);
                string failureKind = panelTimedOut ? "panel timeout" : "panel failed";
                _recorder.RecordAudit(null, model.Name, estimatedTokens, null, estCost, elapsedMs, sessionId,
                    decision.Reason + $"; fusion-router: {failureKind}" + (tripped ? " (circuit tripped)" : ""),
                    false, lastErrorMessage, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: estCost > 0m, fusionRole: "panel",
                    quotaLimited: quotaLimited,
                    reward: failureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
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
        analystModel ??= PickUnfailedFallback(decision.Candidates, failedInThisRequest);

        // 7. 调用 analyst（结构化分析）。
        string analystPrompt = string.IsNullOrWhiteSpace(routing.FusionRouterAnalystPrompt)
            ? FusionSynthesis.DefaultAnalystPrompt
            : routing.FusionRouterAnalystPrompt;

        var analystRequest = FusionSynthesis.BuildAnalystRequest(request, panelAnswers, analystPrompt, routing.FusionRouterTemperature);
        FusionAnalysis? analysis = null;
        long analystElapsedMs = 0;

        var analystSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var analystClient = _clientProvider.GetClient(analystModel);
            var analystResponse = await analystClient.CompleteRawAsync(analystRequest, ct).ConfigureAwait(false);
            analystSw.Stop();
            analystElapsedMs = analystSw.ElapsedMilliseconds;

            ChatUsage? analystUsage = analystResponse.Usage;
            decimal analystCost = analystUsage is not null ? CostCalculator.Compute(analystUsage, analystModel) : 0m;
            if (analystUsage is not null)
                _recorder.RecordCost(analystCost, sessionId);

            _recorder.RecordQuota(analystModel.Name, analystResponse.Metadata);
            _healthTracker.RecordSuccess(analystModel.Name, requiredSuccesses);
            double analystReward = _recorder.RecordThompsonOutcome(
                analystModel.Name,
                analystElapsedMs,
                decision,
                completionTokens: analystUsage?.CompletionTokens ?? 0);
            _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, analystUsage, analystCost, analystElapsedMs, sessionId,
                decision.Reason + "; fusion-router: analyst", true, null, false, routedTier,
                isAdopted: false, parallelGroupId: groupId, isEstimated: false, fusionRole: "analyst",
                timeToFirstTokenMs: analystResponse.Metadata?.ResponseHeaderLatencyMs,
                reward: analystReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);

            analysis = FusionSynthesis.ParseAnalysis(analystResponse);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            analystSw.Stop();
            analystElapsedMs = analystSw.ElapsedMilliseconds;
            bool quotaLimited = ex is ModelClientException
            { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
            double? analystFailureReward = null;
            if (quotaLimited)
            {
                var quotaError = (ModelClientException)ex;
                _recorder.RecordQuota(analystModel.Name, quotaError.Metadata, rateLimited: true);
                failedInThisRequest.Add(analystModel.Name);
            }
            else
            {
                _healthTracker.RecordFailure(analystModel.Name, threshold, cooldown);
                analystFailureReward = _recorder.RecordThompsonOutcome(analystModel.Name, null, decision);
            }
            int status = UpstreamFailureClassifier.GetStatus(ex);
            _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, null, 0m, analystElapsedMs,
                sessionId, decision.Reason + "; fusion-router: analyst failed", false,
                UpstreamFailureClassifier.SafeMessage(ex, quotaLimited), false, routedTier, isAdopted: false,
                parallelGroupId: groupId, fusionRole: "analyst", quotaLimited: quotaLimited,
                reward: analystFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
            _logger.LogWarning("Fusion router analyst call failed (model {Model}, status {Status}), falling back to serial",
                analystModel.Name, status);
            // 配额限流的 analyst 已记入 failedInThisRequest（串行降级不再重试该 429 模型）；
            // 非配额失败经 RecordFailure 计入断路器（若 analyst 恰是候选，FailoverPolicy 按熔断态排除）。
            // 二者均回退串行降级链。
            return new FusionAttemptResult(null, analystModel.Name, status,
                UpstreamFailureClassifier.SafeMessage(ex, status == 429));
        }

        // 分析解析失败（JSON 解析/格式不对）→ P2：response_format 重试一次，仍失败则软降级用原始文本。
        if (analysis is null)
        {
            _logger.LogWarning(
                "Fusion router analyst parse failed (model {Model}), retrying once with response_format=json_object",
                analystModel.Name);
            var retryRequest = FusionSynthesis.BuildAnalystRequest(
                request, panelAnswers, analystPrompt, routing.FusionRouterTemperature, requestJsonFormat: true);

            var retrySw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var analystClient = _clientProvider.GetClient(analystModel);
                var retryResponse = await analystClient.CompleteRawAsync(retryRequest, ct).ConfigureAwait(false);
                retrySw.Stop();
                long retryElapsedMs = retrySw.ElapsedMilliseconds;

                ChatUsage? retryUsage = retryResponse.Usage;
                decimal retryCost = retryUsage is not null ? CostCalculator.Compute(retryUsage, analystModel) : 0m;
                if (retryUsage is not null)
                    _recorder.RecordCost(retryCost, sessionId);
                _recorder.RecordQuota(analystModel.Name, retryResponse.Metadata);
                _healthTracker.RecordSuccess(analystModel.Name, requiredSuccesses);
                double retryReward = _recorder.RecordThompsonOutcome(analystModel.Name, retryElapsedMs, decision, completionTokens: retryUsage?.CompletionTokens ?? 0);
                _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, retryUsage, retryCost, retryElapsedMs,
                    sessionId, decision.Reason + "; fusion-router: analyst retry(parse)", true, null, false, routedTier,
                    isAdopted: false, parallelGroupId: groupId, isEstimated: false, fusionRole: "analyst",
                    timeToFirstTokenMs: retryResponse.Metadata?.ResponseHeaderLatencyMs,
                    reward: retryReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);

                analysis = FusionSynthesis.ParseAnalysis(retryResponse);

                // 重试仍解析失败 → 软降级：用原始文本作 Recommendation，保留已付 panel 成本。
                if (analysis is null)
                {
                    string rawText = ResponseConfidenceChecker.ExtractAssistantText(retryResponse);
                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        _logger.LogWarning(
                            "Fusion router analyst retry parse failed (model {Model}), degrading to raw text recommendation",
                            analystModel.Name);
                        analysis = FusionSynthesis.BuildFallbackAnalysis(rawText);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Fusion router analyst retry produced empty response (model {Model}), falling back to serial",
                            analystModel.Name);
                        return new FusionAttemptResult(null, analystModel.Name, 502, "analyst parse failed (empty retry)");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                retrySw.Stop();
                long retryElapsedMs = retrySw.ElapsedMilliseconds;
                bool retryQuotaLimited = ex is ModelClientException
                { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                double? retryFailureReward = null;
                if (retryQuotaLimited)
                {
                    var quotaError = (ModelClientException)ex;
                    _recorder.RecordQuota(analystModel.Name, quotaError.Metadata, rateLimited: true);
                }
                else
                {
                    _healthTracker.RecordFailure(analystModel.Name, threshold, cooldown);
                    retryFailureReward = _recorder.RecordThompsonOutcome(analystModel.Name, null, decision);
                }
                int retryStatus = UpstreamFailureClassifier.GetStatus(ex);
                _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, null, 0m, retryElapsedMs,
                    sessionId, decision.Reason + "; fusion-router: analyst retry failed", false,
                    UpstreamFailureClassifier.SafeMessage(ex, retryQuotaLimited), false, routedTier, isAdopted: false,
                    parallelGroupId: groupId, fusionRole: "analyst", quotaLimited: retryQuotaLimited,
                    reward: retryFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                _logger.LogWarning(
                    "Fusion router analyst retry failed (model {Model}, status {Status}), falling back to serial",
                    analystModel.Name, retryStatus);
                return new FusionAttemptResult(null, analystModel.Name, retryStatus,
                    UpstreamFailureClassifier.SafeMessage(ex, retryStatus == 429));
            }
        }

        // 8. 解析 outer 模型并调用。
        ModelEndpointOptions? outerModel = null;
        if (!string.IsNullOrWhiteSpace(routing.FusionRouterOuterModel))
        {
            outerModel = options.Models
                .FirstOrDefault(m => m.Enabled && m.Name.Equals(routing.FusionRouterOuterModel, StringComparison.OrdinalIgnoreCase));
        }
        outerModel ??= PickUnfailedFallback(decision.Candidates, failedInThisRequest);

        var outerRequest = FusionSynthesis.BuildOuterRequest(
            request, analysis, FusionSynthesis.DefaultOuterPrompt, routing.FusionRouterMaxOutputTokens);

        var outerSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var outerClient = _clientProvider.GetClient(outerModel);
            var outerResponse = await outerClient.CompleteRawAsync(outerRequest, ct).ConfigureAwait(false);
            outerSw.Stop();

            ChatUsage? outerUsage = outerResponse.Usage;
            decimal outerCost = outerUsage is not null ? CostCalculator.Compute(outerUsage, outerModel) : 0m;
            if (outerUsage is not null)
                _recorder.RecordCost(outerCost, sessionId);

            _recorder.RecordQuota(outerModel.Name, outerResponse.Metadata);
            _healthTracker.RecordSuccess(outerModel.Name, requiredSuccesses);
            double outerReward = _recorder.RecordThompsonOutcome(outerModel.Name, outerSw.ElapsedMilliseconds, decision, completionTokens: outerUsage?.CompletionTokens ?? 0);
            _recorder.RecordAffinity(sessionId, outerModel.Name, AffinitySignal.Weak);
            _recorder.RecordPromptCacheAffinity(request, outerModel.Name);
            _recorder.RecordAudit(null, outerModel.Name, estimatedTokens, outerUsage, outerCost, outerSw.ElapsedMilliseconds, sessionId,
                decision.Reason + "; fusion-router: outer", true, null, false, routedTier,
                isAdopted: true, parallelGroupId: groupId, isEstimated: false, fusionRole: "outer",
                timeToFirstTokenMs: outerResponse.Metadata?.ResponseHeaderLatencyMs,
                reward: outerReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);

            _logger.LogInformation("Fusion router: completed (group {GroupId}), panel={PanelCount}, analyst={Analyst}, outer={Outer}",
                groupId, panelAnswers.Count, analystModel.Name, outerModel.Name);

            return new FusionAttemptResult(outerResponse, outerModel.Name, null, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            outerSw.Stop();
            bool quotaLimited = ex is ModelClientException
            { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
            double? outerFailureReward = null;
            if (quotaLimited)
            {
                var quotaError = (ModelClientException)ex;
                _recorder.RecordQuota(outerModel.Name, quotaError.Metadata, rateLimited: true);
                failedInThisRequest.Add(outerModel.Name);
            }
            else
            {
                _healthTracker.RecordFailure(outerModel.Name, threshold, cooldown);
                outerFailureReward = _recorder.RecordThompsonOutcome(outerModel.Name, null, decision);
            }
            int status = UpstreamFailureClassifier.GetStatus(ex);
            _recorder.RecordAudit(null, outerModel.Name, estimatedTokens, null, 0m, outerSw.ElapsedMilliseconds,
                sessionId, decision.Reason + "; fusion-router: outer failed", false,
                UpstreamFailureClassifier.SafeMessage(ex, quotaLimited), false, routedTier, isAdopted: false,
                parallelGroupId: groupId, fusionRole: "outer", quotaLimited: quotaLimited,
                reward: outerFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
            _logger.LogWarning("Fusion router outer call failed (model {Model}, status {Status}), falling back to serial",
                outerModel.Name, status);
            return new FusionAttemptResult(null, outerModel.Name, status,
                UpstreamFailureClassifier.SafeMessage(ex, status == 429));
        }
    }

    /// <summary>
    /// 挑选 analyst/outer 的回退模型：优先选候选链中本次请求尚未失败的模型。
    /// 失败回退时若 <c>Candidates[0]</c> 恰是刚失败的 panel 模型，analyst/outer 选中它
    /// 大概率再失败（且调用绕过断路器门控），浪费一次往返延迟并多走一次串行降级。
    /// 至少一个 panel 成功（<c>panelAnswers.Count &gt; 0</c>）时，该成功模型在候选链中
    /// 且不在 <c>failedInThisRequest</c>，故总能找到；全失败时回退首候选保底。
    /// </summary>
    private static ModelEndpointOptions PickUnfailedFallback(
        IReadOnlyList<ModelEndpointOptions> candidates,
        HashSet<string> failedInThisRequest)
        => candidates.FirstOrDefault(m => !failedInThisRequest.Contains(m.Name))
           ?? candidates[0];

    /// <summary>
    /// 流式融合路由（Progressive Speculative Streaming）：
    /// Anchor 锚点首选模型开启首发 SSE 实时推流（TTFT &lt; 200ms），后台并发运行 Secondary Panel 模型与 Analyst 分析。
    /// 若 Analyst 分析识别出深度补充或矛盾分歧，在 SSE 结尾追加融合修正 Patch Chunk。
    /// </summary>
    public async IAsyncEnumerable<RawStreamLine> ExecuteStreamAsync(
        ChatRequest request,
        RouterOptions options,
        RouterDecision decision,
        int estimatedTokens,
        ModelTier routedTier,
        string? sessionId,
        HashSet<string> failedInThisRequest,
        List<string> attemptedModels,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var routing = options.Routing;
        var panelSelection = _panelSelector.Select(decision, routing, _quotaStore);
        int panelSize = panelSelection.RequestedSize;
        int halfOpenMaxProbes = routing.FailoverHalfOpenMaxProbes;
        if (panelSelection.RankedCandidates.Count < 2)
            yield break;

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

        // 准入：逐候选占用半开探测槽位（与非流式 ExecuteAsync 一致），防止半开态模型被并发融合流过量打入。
        var admitted = new List<ModelEndpointOptions>();
        foreach (var candidate in panelSelection.RankedCandidates)
        {
            if (admitted.Count >= panelSize) break;
            if (!_healthTracker.TryBeginProbe(candidate.Name, halfOpenMaxProbes))
                continue;
            admitted.Add(candidate);
            attemptedModels.Add(candidate.Name);
        }
        // 准入回退：释放所有已占探测槽位并回滚 attemptedModels（与 ExecuteAsync 的 admitted<2 路径对称）。
        // anchor 客户端/流创建抛出或凑不齐并行数时调用，避免半开探测槽位泄漏
        // （此前 anchor 设置失败直接 yield break 会泄漏 anchor + 尚未启动的 secondary 全部槽位）。
        void ReleaseAdmitted()
        {
            foreach (var m in admitted)
            {
                _healthTracker.ReleaseProbe(m.Name);
                attemptedModels.Remove(m.Name);
            }
        }

        if (admitted.Count < 2)
        {
            ReleaseAdmitted();
            yield break;
        }
        var anchorModel = admitted[0];

        // 1. 首发 Anchor 模型开始流式输出
        IModelClient anchorClient;
        try
        {
            anchorClient = _clientProvider.GetClient(anchorModel);
        }
        catch
        {
            // 准入已为 anchor 及尚未启动的 secondary 占槽位：设置失败须全部释放，否则半开槽位永久滞留。
            ReleaseAdmitted();
            yield break;
        }

        IAsyncEnumerable<RawStreamLine> anchorStream;
        try
        {
            anchorStream = anchorClient.StreamRawAsync(request, ct);
        }
        catch
        {
            ReleaseAdmitted();
            yield break;
        }

        // 2. 后台并发运行 Secondary Panel 任务（非流式获取其他 panel 补充答案）
        // secondary 模型已在准入阶段计入 attemptedModels，此处不再重复添加（此前为重复项）。
        var secondaryModels = admitted.Skip(1).ToList();
        var secondaryTasks = new List<Task<(string Model, string Text)>>();
        foreach (var m in secondaryModels)
        {
            secondaryTasks.Add(Task.Run(async () =>
            {
                var secondarySw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = _clientProvider.GetClient(m);
                    var resp = await client.CompleteRawAsync(request with { Stream = false }, ct).ConfigureAwait(false);
                    secondarySw.Stop();
                    ChatUsage? usage = resp.Usage;
                    decimal cost = usage is not null ? CostCalculator.Compute(usage, m) : 0m;
                    if (usage is not null)
                        _recorder.RecordCost(cost, sessionId);
                    _recorder.RecordQuota(m.Name, resp.Metadata);
                    _healthTracker.RecordSuccess(m.Name, routing.FailoverHalfOpenRequiredSuccesses);
                    double secondaryReward = _recorder.RecordThompsonOutcome(m.Name, secondarySw.ElapsedMilliseconds, decision, completionTokens: usage?.CompletionTokens ?? 0);
                    _recorder.RecordAudit(null, m.Name, estimatedTokens, usage, cost,
                        secondarySw.ElapsedMilliseconds, sessionId, decision.Reason + "; fusion-stream: secondary",
                        true, null, true, routedTier, isAdopted: false, fusionRole: "secondary",
                        timeToFirstTokenMs: resp.Metadata?.ResponseHeaderLatencyMs,
                        reward: secondaryReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                    return (m.Name, ResponseConfidenceChecker.ExtractAssistantText(resp));
                }
                catch (Exception ex)
                {
                    secondarySw.Stop();
                    bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(ex);
                    double? secondaryFailureReward = null;
                    if (quotaLimited)
                    {
                        _recorder.RecordQuota(m.Name, ((ModelClientException)ex).Metadata, rateLimited: true);
                        // 配额限流非模型健康信号：仅释放准入时占用的探测槽位（此前为泄漏路径）。
                        _healthTracker.ReleaseProbe(m.Name);
                    }
                    else if (!ct.IsCancellationRequested)
                    {
                        _healthTracker.RecordFailure(m.Name, routing.FailoverFailureThreshold, routing.FailoverCooldownSeconds);
                        secondaryFailureReward = _recorder.RecordThompsonOutcome(m.Name, null, decision);
                    }
                    else
                    {
                        // 客户端取消：非模型健康信号，释放槽位（此前为泄漏路径）。
                        _healthTracker.ReleaseProbe(m.Name);
                    }
                    _recorder.RecordAudit(null, m.Name, estimatedTokens, null, 0m,
                        secondarySw.ElapsedMilliseconds, sessionId, decision.Reason + "; fusion-stream: secondary failed",
                        false, UpstreamFailureClassifier.SafeMessage(ex, quotaLimited), true, routedTier,
                        isAdopted: false, fusionRole: "secondary", quotaLimited: quotaLimited,
                        reward: secondaryFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                    return (m.Name, string.Empty);
                }
            }, ct));
        }

        var anchorTextSb = new StringBuilder();
        RawStreamLine? lastLine = null;
        long anchorElapsedMs = 0;
        bool anchorStreamCompleted = false;
        ChatUsage? anchorUsage = null;

        // 3. 实时流式输出 Anchor 模型内容（try-finally：IAsyncEnumerable 禁止 try-catch 含 yield，
        //    但允许 try-finally。finally 内同步记 audit/health，让融合流请求进入审计与断路器统计）。
        var anchorSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var line in anchorStream.WithCancellation(ct))
            {
                // 捕获末次 usage（OpenAI 通常在末块或 [DONE] 前一块携带），用于成本入账（此前被丢弃导致日预算低估）。
                if (line.Usage is not null)
                    anchorUsage = line.Usage;
                if (line.Data == "[DONE]")
                {
                    lastLine = line;
                    break;
                }
                if (!string.IsNullOrEmpty(line.Data))
                {
                    anchorTextSb.Append(line.Data);
                }
                yield return line;
            }
            anchorStreamCompleted = true;
        }
        finally
        {
            anchorSw.Stop();
            anchorElapsedMs = anchorSw.ElapsedMilliseconds;
            if (anchorStreamCompleted)
            {
                // anchor 成本按真实 usage 计；无 usage 时记 0 并标 IsEstimated=false（与主链流式口径一致）。
                decimal anchorCost = anchorUsage is not null ? CostCalculator.Compute(anchorUsage, anchorModel) : 0m;
                if (anchorUsage is not null)
                    _recorder.RecordCost(anchorCost, sessionId);
                _healthTracker.RecordSuccess(anchorModel.Name, routing.FailoverHalfOpenRequiredSuccesses);
                double anchorReward = _recorder.RecordThompsonOutcome(anchorModel.Name, anchorElapsedMs, decision, completionTokens: anchorUsage?.CompletionTokens ?? 0);
                _recorder.RecordAudit(null, anchorModel.Name, estimatedTokens, anchorUsage, anchorCost,
                    anchorElapsedMs, sessionId, "fusion-stream-anchor", true, null, true, routedTier,
                    isEstimated: anchorUsage is null,
                    reward: anchorReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
            }
            else
            {
                if (!ct.IsCancellationRequested)
                {
                    // 真实故障（非客户端取消）：计入断路器 + 审计（RecordFailure 顺带释放准入时占用的探测槽位）。
                    bool tripped = _healthTracker.RecordFailure(anchorModel.Name, routing.FailoverFailureThreshold, routing.FailoverCooldownSeconds);
                    double anchorFailureReward = _recorder.RecordThompsonOutcome(anchorModel.Name, null, decision);
                    _recorder.RecordAudit(null, anchorModel.Name, estimatedTokens, null, 0m,
                        anchorElapsedMs, sessionId, "fusion-stream-anchor", false, "anchor-stream-faulted", true, routedTier,
                        reward: anchorFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                    _logger.LogWarning("Fusion anchor {Name} stream faulted{Tripped}", anchorModel.Name, tripped ? " (circuit tripped)" : "");
                }
                else
                {
                    // 客户端取消：非模型健康信号，仅释放准入时占用的探测槽位（此前为泄漏路径）。
                    _healthTracker.ReleaseProbe(anchorModel.Name);
                }

                // anchor 中途故障/取消：控制流不再到达下方的 secondary 收集块（Task.WhenAll），
                // 在此观察已启动的 secondary 任务，避免它们成为孤儿 fire-and-forget（结果丢弃，
                // 但各 task 自行释放探测槽位/记审计；secondary 共用同一 ct，取消时快速收尾）。
                try
                {
                    await Task.WhenAll(secondaryTasks).ConfigureAwait(false);
                }
                catch
                {
                    // secondary tasks 内部已 try-catch 并始终返回元组，不应抛；防御性吞掉。
                }
            }
        }

        // 4. Anchor 流式完成后，收集 Secondary Panel 回答并执行轻量 Analyst 评估
        string? patchJson = null;
        try
        {
            var secondaryResults = await Task.WhenAll(secondaryTasks).ConfigureAwait(false);
            var panelAnswers = new List<(string Model, string Text)> { (anchorModel.Name, anchorTextSb.ToString()) };
            foreach (var secRes in secondaryResults)
            {
                if (!string.IsNullOrWhiteSpace(secRes.Text))
                    panelAnswers.Add(secRes);
            }

            if (panelAnswers.Count >= 2)
            {
                ModelEndpointOptions analystModel = options.Models.FirstOrDefault(m => m.Enabled && m.Name.Equals(routing.FusionRouterAnalystModel, StringComparison.OrdinalIgnoreCase))
                    ?? PickUnfailedFallback(decision.Candidates, failedInThisRequest);

                string analystPrompt = string.IsNullOrWhiteSpace(routing.FusionRouterAnalystPrompt)
                    ? FusionSynthesis.DefaultAnalystPrompt
                    : routing.FusionRouterAnalystPrompt;

                var analystReq = FusionSynthesis.BuildAnalystRequest(request, panelAnswers, analystPrompt, routing.FusionRouterTemperature);
                attemptedModels.Add(analystModel.Name);
                var analystSw = System.Diagnostics.Stopwatch.StartNew();
                RawChatResponse analystResp;
                try
                {
                    var analystClient = _clientProvider.GetClient(analystModel);
                    analystResp = await analystClient.CompleteRawAsync(analystReq, ct).ConfigureAwait(false);
                    analystSw.Stop();
                    ChatUsage? usage = analystResp.Usage;
                    decimal cost = usage is not null ? CostCalculator.Compute(usage, analystModel) : 0m;
                    if (usage is not null)
                        _recorder.RecordCost(cost, sessionId);
                    _recorder.RecordQuota(analystModel.Name, analystResp.Metadata);
                    _healthTracker.RecordSuccess(analystModel.Name, routing.FailoverHalfOpenRequiredSuccesses);
                    double streamAnalystReward = _recorder.RecordThompsonOutcome(analystModel.Name, analystSw.ElapsedMilliseconds, decision);
                    _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, usage, cost,
                        analystSw.ElapsedMilliseconds, sessionId, decision.Reason + "; fusion-stream: analyst",
                        true, null, true, routedTier, isAdopted: false, fusionRole: "analyst",
                        timeToFirstTokenMs: analystResp.Metadata?.ResponseHeaderLatencyMs,
                        reward: streamAnalystReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                }
                catch (Exception ex)
                {
                    analystSw.Stop();
                    bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(ex);
                    double? streamAnalystFailureReward = null;
                    if (quotaLimited)
                    {
                        _recorder.RecordQuota(analystModel.Name, ((ModelClientException)ex).Metadata, rateLimited: true);
                    }
                    else if (!ct.IsCancellationRequested)
                    {
                        _healthTracker.RecordFailure(analystModel.Name, routing.FailoverFailureThreshold, routing.FailoverCooldownSeconds);
                        streamAnalystFailureReward = _recorder.RecordThompsonOutcome(analystModel.Name, null, decision);
                    }
                    _recorder.RecordAudit(null, analystModel.Name, estimatedTokens, null, 0m,
                        analystSw.ElapsedMilliseconds, sessionId, decision.Reason + "; fusion-stream: analyst failed",
                        false, UpstreamFailureClassifier.SafeMessage(ex, quotaLimited), true, routedTier,
                        isAdopted: false, fusionRole: "analyst", quotaLimited: quotaLimited,
                        reward: streamAnalystFailureReward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: requestContent);
                    throw;
                }
                var analysis = FusionSynthesis.ParseAnalysis(analystResp);

                if (analysis is not null && (!string.IsNullOrWhiteSpace(analysis.Contradictions) || !string.IsNullOrWhiteSpace(analysis.UniqueInsights) || !string.IsNullOrWhiteSpace(analysis.Gaps)))
                {
                    var patchSb = new StringBuilder();
                    patchSb.AppendLine("\n\n---\n💡 **多模型融合增强分析**：");
                    if (!string.IsNullOrWhiteSpace(analysis.UniqueInsights))
                        patchSb.AppendLine($"- **关键补充**：{analysis.UniqueInsights}");
                    if (!string.IsNullOrWhiteSpace(analysis.Contradictions))
                        patchSb.AppendLine($"- **主要分歧**：{analysis.Contradictions}");
                    if (!string.IsNullOrWhiteSpace(analysis.Gaps))
                        patchSb.AppendLine($"- **注意事项**：{analysis.Gaps}");

                    patchJson = CreateDeltaChunkJson(patchSb.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background streaming fusion synthesis skipped or errored.");
        }

        if (!string.IsNullOrEmpty(patchJson))
        {
            yield return new RawStreamLine(patchJson, null);
        }

        // 5. 补发 [DONE] 标识
        yield return lastLine ?? new RawStreamLine("[DONE]", null);
    }

    private static string CreateDeltaChunkJson(string text)
    {
        string escaped = JsonSerializer.Serialize(text);
        return $"{{\"id\":\"fusion-patch-{Guid.NewGuid():N}\",\"object\":\"chat.completion.chunk\",\"created\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"choices\":[{{\"index\":0,\"delta\":{{\"content\":{escaped}}},\"finish_reason\":null}}]}}";
    }
}
