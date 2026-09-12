using System.Diagnostics;
using OptiRouter.Clients;
using OptiRouter.Compliance;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 请求生命周期结算组件（#4 模块化：从 ProxyOrchestrator 拆出的分部类）。
/// 候选失败（五类）、成功、流式异常终态（故障/取消/断开）、最终输出审核出口
/// 与单轮结算快照在此一处定义——终态矩阵的单一修改点。
/// </summary>
public sealed partial class ProxyOrchestrator
{
    /// <summary>
    /// 生命周期收敛（切片③）：流式候选异常终态结算统一出口——中途故障（计入熔断）、
    /// 客户端取消/提前断开（P1-6：不进熔断不记负反馈，已知 usage 一次性结算）、
    /// 未产生健康信号（首行前取消/空流：仅释放探槽）。四分支此前内联在迭代器 finally 中。
    /// </summary>
    private void SettleStreamAbnormalEnd(
        in RequestSnapshot snapshot,
        ModelEndpointOptions candidate,
        RouterDecision decision,
        bool streamFaulted,
        bool clientCancelled,
        bool hasFirstLine,
        ChatUsage? finalUsage,
        RawStreamLine firstLine,
        System.Diagnostics.Stopwatch attemptSw)
    {
        attemptSw.Stop();

        if (streamFaulted)
        {
            // 中途失败计入断路器统计（与非流式失败同等对待）。
            bool tripped = _healthTracker.RecordFailure(candidate.Name, snapshot.FailureThreshold, snapshot.CooldownSeconds);
            double reward = _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
            _regenerateTracker.Record(snapshot.FeedbackKey, candidate.Name, success: false);
            _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                attemptSw.ElapsedMilliseconds, snapshot.SessionId, decision.Reason, false, "stream-faulted", true, snapshot.RoutedTier,
                reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: snapshot.RequestContent, classificationSignal: decision.ClassificationSignal);
            _logger.LogWarning("Streaming model {Name} failed mid-stream{Tripped}",
                candidate.Name, tripped ? " (circuit tripped)" : "");
            return;
        }

        if (hasFirstLine)
        {
            // P1-6：客户端取消 / 提前断开不是上游失败——不进熔断、不记负反馈。
            // usage 行已到达的消费仍须一次性结算（供应商侧可能已按生成量计费），
            // 不重复计费（正常结束路径不会再走这里）。
            string terminal = clientCancelled ? "client-cancelled" : "client-disconnected";
            if (finalUsage is not null)
            {
                decimal cost = CostCalculator.Compute(finalUsage, candidate);
                _recorder.RecordCost(cost, snapshot.SessionId);
                _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, finalUsage, cost,
                    attemptSw.ElapsedMilliseconds, snapshot.SessionId, decision.Reason, false, terminal, true, snapshot.RoutedTier,
                    timeToFirstTokenMs: firstLine.Metadata?.TimeToFirstTokenMs,
                    epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: snapshot.RequestContent, classificationSignal: decision.ClassificationSignal);
            }
            else
            {
                _recorder.RecordAudit(null, candidate.Name, decision.EstimatedInputTokens, null, 0m,
                    attemptSw.ElapsedMilliseconds, snapshot.SessionId, decision.Reason, false, terminal, true, snapshot.RoutedTier,
                    requestContent: snapshot.RequestContent);
            }
            _healthTracker.ReleaseProbe(candidate.Name);
            return;
        }

        // 无健康信号（不可重试错误、首行前外部取消、空流）：仅释放探测槽位。
        _healthTracker.ReleaseProbe(candidate.Name);
    }

    /// <summary>
    /// 生命周期收敛（切片②）：非流式候选成功结算统一出口——成本（usage 精确/输入估算两口径）、
    /// 质量因子折减 reward、Thompson、会话/提示缓存亲和、审计、配额、熔断成功。修复前此套
    /// 记账内联在候选循环中，与流式路径及失败路径各持一份变体。
    /// </summary>
    private decimal? SettleCandidateSuccess(
        in RequestSnapshot snapshot,
        ModelEndpointOptions candidate,
        RouterDecision decision,
        ChatRequest request,
        RawChatResponse response,
        int estimatedTokens,
        long elapsedMs,
        int halfOpenRequiredSuccesses,
        double qualityPenaltyFactor)
    {
        decimal cost = response.Usage is not null
            ? CostCalculator.Compute(response.Usage, candidate)
            : 0m;
        // 质量因子：从非流式响应检测低质量信号（截断/空答/JSON 契约违约），乘性折减延迟 reward。
        double qualityFactor = OutcomeRecorder.ExtractQualityFactor(response, qualityPenaltyFactor, request);
        double reward = _recorder.RecordThompsonOutcome(candidate.Name, elapsedMs, decision, cost,
            actualTier: candidate.Tier, qualityFactor: qualityFactor, completionTokens: response.Usage?.CompletionTokens ?? 0);
        _recorder.RecordAffinity(snapshot.SessionId, candidate.Name, AffinitySignal.Strong, elapsedMs);
        _recorder.RecordPromptCacheAffinity(request, candidate.Name);

        if (response.Usage is not null)
        {
            _recorder.RecordCost(cost, snapshot.SessionId);
            _recorder.RecordAudit(null, candidate.Name, estimatedTokens, response.Usage, cost, elapsedMs, snapshot.SessionId, decision.Reason, true, null, false, snapshot.RoutedTier,
                timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: snapshot.RequestContent, classificationSignal: decision.ClassificationSignal);
        }
        else
        {
            // 上游未返回 usage：无法精确计费。按估算 input 成本入账并标 IsEstimated，
            // 与失败/取消路径（RaceOrchestrator/FusionRouter）的估算口径一致，
            // 避免成功请求被记 0 成本导致日/会话预算低估。
            decimal estCost = OutcomeRecorder.EstimateInputCost(candidate, estimatedTokens);
            if (estCost > 0m)
                _recorder.RecordCost(estCost, snapshot.SessionId);
            _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, estCost, elapsedMs, snapshot.SessionId, decision.Reason, true, null, false, snapshot.RoutedTier,
                isEstimated: estCost > 0m,
                timeToFirstTokenMs: response.Metadata?.ResponseHeaderLatencyMs, reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel, requestContent: snapshot.RequestContent, classificationSignal: decision.ClassificationSignal);
        }
        _recorder.RecordQuota(candidate.Name, response.Metadata);
        _healthTracker.RecordSuccess(candidate.Name, halfOpenRequiredSuccesses);
        return response.Usage is not null ? cost : null;
    }

    /// <summary>
    /// 生命周期收敛（结算组件种子）：串行/降级路径的候选失败结算统一出口——Thompson 惩罚、
    /// regenerate 负反馈、审计、熔断/探槽处理按失败类别在一处表达，控制流
    /// （换下一候选/透传原始状态码/全局超时终止）留在调用方。修复前五类失败的记账
    /// 分散在五个近乎复制的 catch 块，新增失败类别时极易漏记某一维度。
    /// 参数束是后续 RequestSnapshot 提取的雏形（收敛后归并为快照对象）。
    /// 返回 (上次失败状态码, 上次失败信息, 是否触发熔断)。
    /// </summary>
    private (int StatusCode, string ErrorMessage, bool Tripped) SettleCandidateFailure(
        CandidateFailureKind kind,
        ModelEndpointOptions candidate,
        RouterDecision decision,
        int estimatedTokens,
        long elapsedMs,
        in RequestSnapshot snapshot,
        Exception exception,
        bool globalTimeout,
        bool hasOtherCandidates)
    {
        int statusCode;
        string errorMessage;
        string auditFailure;
        bool recordCircuitFailure;

        switch (kind)
        {
            case CandidateFailureKind.QuotaLimited:
                var quotaError = (ModelClientException)exception;
                _recorder.RecordQuota(candidate.Name, quotaError.Metadata, rateLimited: true);
                statusCode = 429;
                errorMessage = "quota-exhausted";
                auditFailure = "quota-exhausted";
                recordCircuitFailure = false;
                break;

            case CandidateFailureKind.RequestRejection:
                statusCode = (int)((ModelClientException)exception).StatusCode;
                errorMessage = $"upstream-status-{statusCode}";
                auditFailure = errorMessage;
                recordCircuitFailure = false;
                break;

            case CandidateFailureKind.UpstreamStatus:
                statusCode = (int)((ModelClientException)exception).StatusCode;
                errorMessage = $"upstream-status-{statusCode}";
                auditFailure = errorMessage;
                recordCircuitFailure = true;
                break;

            case CandidateFailureKind.NetworkError:
                statusCode = 503;
                errorMessage = "network-error";
                auditFailure = "network-error";
                recordCircuitFailure = true;
                break;

            default: // InternalTimeout
                statusCode = 408;
                errorMessage = globalTimeout
                    ? $"Global failover timeout ({snapshot.GlobalTimeoutSeconds}s) exceeded."
                    : "Request timed out inside the proxy.";
                auditFailure = globalTimeout ? "global-failover-timeout" : "timeout";
                recordCircuitFailure = true;
                break;
        }

        bool tripped;
        if (recordCircuitFailure)
        {
            tripped = _healthTracker.RecordFailure(candidate.Name, snapshot.FailureThreshold, snapshot.CooldownSeconds);
        }
        else
        {
            tripped = false;
            _healthTracker.ReleaseProbe(candidate.Name);
        }

        // 429 视为纯配额：不入断路器，也不给 Thompson 负反馈（quota 状态与模型质量无关）。
        double? reward = kind == CandidateFailureKind.QuotaLimited
            ? null
            : _recorder.RecordThompsonOutcome(candidate.Name, null, decision);
        _regenerateTracker.Record(snapshot.FeedbackKey, candidate.Name, success: false);
        _recorder.RecordAudit(null, candidate.Name, estimatedTokens, null, 0m, elapsedMs, snapshot.SessionId,
            decision.Reason, false, auditFailure, false, snapshot.RoutedTier,
            quotaLimited: kind == CandidateFailureKind.QuotaLimited,
            reward: reward, epsilonPromotedModel: decision.EpsilonPromotedModel,
            requestContent: snapshot.RequestContent, classificationSignal: decision.ClassificationSignal);

        switch (kind)
        {
            case CandidateFailureKind.QuotaLimited:
                _logger.LogWarning("Model {Name} quota exhausted (status {Status}), trying next candidate", candidate.Name, 429);
                break;
            case CandidateFailureKind.RequestRejection:
                _logger.LogWarning("Model {Name} rejected request (status {Status}){Action}",
                    candidate.Name, ((ModelClientException)exception).StatusCode, hasOtherCandidates ? ", trying next candidate" : ", propagating to client");
                break;
            case CandidateFailureKind.UpstreamStatus:
                _logger.LogWarning("Model {Name} failed (status {Status}), trying next candidate{Tripped}",
                    candidate.Name, ((ModelClientException)exception).StatusCode, tripped ? " (circuit tripped)" : "");
                break;
            case CandidateFailureKind.NetworkError:
                _logger.LogWarning(exception, "Model {Name} network request failed, trying next candidate{Tripped}",
                    candidate.Name, tripped ? " (circuit tripped)" : "");
                break;
            default:
                _logger.LogWarning("Model {Name} timed out ({Reason}), trying next{Tripped}",
                    candidate.Name, globalTimeout ? "global failover timeout" : "timeout", tripped ? " (circuit tripped)" : "");
                break;
        }

        return (statusCode, errorMessage, tripped);
    }

    /// <summary>候选失败的结算类别。控制流差异（透传/终止）不在此枚举内，由调用方按原语义处理。</summary>
    private enum CandidateFailureKind
    {
        /// <summary>429 配额耗尽：仅记配额与探槽释放，不入熔断、不记 Thompson。</summary>
        QuotaLimited,

        /// <summary>请求语义类拒绝（400/422/413...）：不入熔断。</summary>
        RequestRejection,

        /// <summary>可重试上游状态（5xx/408）与凭证错误：计入熔断。</summary>
        UpstreamStatus,

        /// <summary>网络异常：计入熔断。</summary>
        NetworkError,

        /// <summary>代理内部超时 / 全局 Failover 超时（非外部取消）：计入熔断。</summary>
        InternalTimeout,
    }

    /// <summary>
    /// 生命周期收敛（切片④）：单轮路由决策的结算上下文快照（不可变）。每轮 while 迭代
    /// 产出一份（routedTier 随轮次候选变化，故按轮而非按请求），结算方法统一以
    /// <c>in snapshot</c> 取参，消除散装参数束。
    /// </summary>
    private readonly record struct RequestSnapshot(
        string? SessionId,
        string? RequestContent,
        string? FeedbackKey,
        ModelTier RoutedTier,
        int GlobalTimeoutSeconds,
        int FailureThreshold,
        int CooldownSeconds);

    /// <summary>
    /// 最终响应输出审核的统一出口：串行、Fusion（quality router）、Fusion-lite（race）、
    /// Cascade 升级四个非流式成功路径都必须经过，违规记审计并按
    /// <see cref="Configuration.RoutingOptions.ModerationOutputAction"/> 为 Block 时抛
    /// <see cref="OptiRouter.Compliance.ComplianceViolationException"/>。
    /// 各模式的真实调用计费/学习反馈在各自路径已完成，不受此处阻断影响。
    /// </summary>
    private async Task ModerateFinalOutputAsync(
        RawChatResponse response,
        RouterOptions options,
        ModelTier routedTier,
        string? sessionId,
        string? requestContent,
        CancellationToken ct)
    {
        if (!options.Routing.EnableContentModeration || _contentModerator is null
            || options.Routing.ModerationOutputAction == OptiRouter.Compliance.ModerationAction.None
            || !ShouldModerate(options.Routing))
        {
            return;
        }

        string? outputText = ExtractContentText(response.Body);
        if (string.IsNullOrWhiteSpace(outputText))
            return;

        var modResult = await _contentModerator.ModerateTextAsync(
            outputText, OptiRouter.Compliance.ModerationDirection.Output, ct).ConfigureAwait(false);
        if (!modResult.IsViolation)
            return;

        _logger.LogWarning("Output blocked by content moderation: category={Category}, score={Score:F3}", modResult.Category, modResult.Score);
        _recorder.RecordAudit(null, "moderation", 0, null, 0m, 0, sessionId, $"moderation-output-blocked:{modResult.Category}", false, modResult.Reason, false, routedTier, requestContent: requestContent);
        if (options.Routing.ModerationOutputAction == OptiRouter.Compliance.ModerationAction.Block)
        {
            throw new OptiRouter.Compliance.ComplianceViolationException(
                $"Output blocked by content moderation (category: {modResult.Category}).", modResult.Category);
        }
    }

}
