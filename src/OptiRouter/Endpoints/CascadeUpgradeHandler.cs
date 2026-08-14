using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// Cheap→Strong 级联自校验处理器。
/// 当首轮命中 Cheap 模型且采样命中（<see cref="RoutingOptions.CascadeUpgradeSampleRate"/>）时，
/// 用同一 Cheap 模型复核答案置信度；低置信则升级到首个可用 Strong 模型重答。
/// 全程异常吞掉返回 null——级联是质量兜底，不应破坏已成功的请求。
/// </summary>
public sealed class CascadeUpgradeHandler
{
    private readonly IModelClientProvider _clientProvider;
    private readonly OutcomeRecorder _recorder;
    private readonly ModelHealthTracker _healthTracker;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ILogger<CascadeUpgradeHandler> _logger;

    public CascadeUpgradeHandler(
        IModelClientProvider clientProvider,
        OutcomeRecorder recorder,
        ModelHealthTracker healthTracker,
        IOptionsMonitor<RouterOptions> options,
        ILogger<CascadeUpgradeHandler> logger)
    {
        _clientProvider = clientProvider;
        _recorder = recorder;
        _healthTracker = healthTracker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 尝试级联自校验与升级。返回 null 表示不升级（用原 Cheap 答案）；返回 <see cref="RawChatResponse"/> 表示升级到 Strong 的重答结果。
    /// 触发条件：<see cref="RoutingOptions.EnableCascadeUpgrade"/> 且采样命中。自校验用同 Cheap 模型，低置信则升级候选链首个 Strong。
    /// 全程异常吞掉返回 null：质量兜底不应破坏已成功的请求。
    /// </summary>
    public async Task<RawChatResponse?> TryUpgradeAsync(
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

        // 他评校验模型：配置 CascadeUpgradeVerifierModel 时按名匹配已启用模型；找不到则告警回退自评（cheapModel）。
        // 消除"模型自评"的自利偏差——用一个（通常更强的）异模型校验 Cheap 答案，置信度判定更可信。
        ModelEndpointOptions verifierModel = cheapModel;
        string? configuredVerifier = routing.CascadeUpgradeVerifierModel;
        bool peerReview = false;
        if (!string.IsNullOrWhiteSpace(configuredVerifier))
        {
            var resolved = _options.CurrentValue.Models
                .FirstOrDefault(m => m.Enabled && string.Equals(m.Name, configuredVerifier, StringComparison.OrdinalIgnoreCase));
            if (resolved is not null)
            {
                verifierModel = resolved;
                peerReview = !string.Equals(resolved.Name, cheapModel.Name, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                _logger.LogWarning("CascadeUpgradeVerifierModel '{Verifier}' 未找到或未启用，回退自评（cheap={Cheap})",
                    configuredVerifier, cheapModel.Name);
            }
        }

        var verifySw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var verifierClient = _clientProvider.GetClient(verifierModel);
            var verifyResponse = await verifierClient.CompleteAsync(verifyRequest, ct).ConfigureAwait(false);
            verifySw.Stop();

            bool confident = ResponseConfidenceChecker.IsConfident(verifyResponse);
            // 复核调用真实消耗 token，必须按实际校验模型价格入账成本账本，否则开级联时预算系统性偏低（漂移）。
            decimal verifyCost = verifyResponse.Usage is not null
                ? CostCalculator.Compute(verifyResponse.Usage, verifierModel)
                : 0m;
            if (verifyResponse.Usage is not null)
                _recorder.RecordCost(verifyCost, sessionId);

            // 校验调用的健康/延迟 reward 记到实际校验模型（verifier）；质量信号（confident/uncertain）记到 Cheap 模型。
            _healthTracker.RecordSuccess(verifierModel.Name, routing.FailoverHalfOpenRequiredSuccesses);
            _recorder.RecordThompsonOutcome(
                verifierModel.Name,
                verifySw.ElapsedMilliseconds,
                decision);

            string verifyKind = peerReview ? "peer-verify" : "self-verify";
            _recorder.RecordAudit(null, verifierModel.Name, estimatedTokens, verifyResponse.Usage, verifyCost, verifySw.ElapsedMilliseconds, sessionId,
                decision.Reason + "; cascade: " + verifyKind + " " + (confident ? "confident" : "uncertain"),
                true, null, false, routedTier, cascadeTriggered: true);

            // 质量信号接入学习状态：自校验置信度此前被丢弃，导致 Thompson/Bandit 系统性偏好"快但不准"的 Cheap。
            // 置信=答案质量高→正反馈强化；不置信→负反馈惩罚，降低后续对该 Cheap 的偏好。reward 值可配置。
            _recorder.RecordQualityOutcome(
                cheapModel.Name,
                confident ? routing.CascadeUpgradeConfidentReward : routing.CascadeUpgradeUncertainReward,
                decision);

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

                decimal upgradeCost = strongResponse.Usage is not null
                    ? CostCalculator.Compute(strongResponse.Usage, upgradeTarget)
                    : 0m;
                if (strongResponse.Usage is not null)
                {
                    _recorder.RecordCost(upgradeCost, sessionId);
                }
                _recorder.RecordQuota(upgradeTarget.Name, strongResponse.Metadata);
                _healthTracker.RecordSuccess(upgradeTarget.Name,
                    routing.FailoverHalfOpenRequiredSuccesses);
                _recorder.RecordThompsonOutcome(
                    upgradeTarget.Name,
                    strongSw.ElapsedMilliseconds,
                    decision);
                _recorder.RecordAffinity(sessionId, upgradeTarget.Name, AffinitySignal.Weak);
                _recorder.RecordPromptCacheAffinity(originalRequest, upgradeTarget.Name);

                _recorder.RecordAudit(null, upgradeTarget.Name, estimatedTokens, strongResponse.Usage,
                    upgradeCost,
                    strongSw.ElapsedMilliseconds, sessionId, decision.Reason + "; cascade: upgraded from " + cheapModel.Name,
                    true, null, false, routedTier, cascadeTriggered: true, upgradedFrom: cheapModel.Name,
                    timeToFirstTokenMs: strongResponse.Metadata?.ResponseHeaderLatencyMs);

                _logger.LogInformation("Cascade upgrade: {Cheap} -> {Strong} (self-verify uncertain)",
                    cheapModel.Name, upgradeTarget.Name);

                return strongResponse;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (ex is ModelClientException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } quotaError)
                {
                    _recorder.RecordQuota(upgradeTarget.Name, quotaError.Metadata, rateLimited: true);
                }
                else
                {
                    _healthTracker.RecordFailure(
                        upgradeTarget.Name,
                        routing.FailoverFailureThreshold,
                        routing.FailoverCooldownSeconds);
                    _recorder.RecordThompsonOutcome(upgradeTarget.Name, null, decision);
                }
                // 升级调用失败（含客户端内部超时）：记录但不抛，返回 null 让调用方用原 Cheap 答案（已有，质量兜底不优于崩溃）。
                // 仅放行外界取消；内部超时不破坏已成功的 Cheap 请求，也不污染 Cheap 熔断。
                _logger.LogWarning(ex, "Cascade upgrade to {Strong} failed, returning cheap answer", upgradeTarget.Name);
                return null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 自校验失败：429 视为纯配额（仅记配额，不污染断路器）；
            // 其余（5xx 等真实上游错误）视为可用性信号，计入断路器与 Thompson 反馈——
            // 主请求成功但紧接着 verify 5xx，说明模型/上游正在劣化。
            // 参见 CascadeCostAccountingTests.Cascade_VerificationFailure_Only5xxUpdatesHealthAndThompson。
            if (ex is ModelClientException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } quotaError)
            {
                _recorder.RecordQuota(verifierModel.Name, quotaError.Metadata, rateLimited: true);
            }
            else
            {
                _healthTracker.RecordFailure(
                    verifierModel.Name,
                    routing.FailoverFailureThreshold,
                    routing.FailoverCooldownSeconds);
                _recorder.RecordThompsonOutcome(verifierModel.Name, null, decision);
            }
            // 自校验本身失败（含客户端内部超时）：吞掉，用原 Cheap 答案。级联是优化路径，非主流程。
            // 仅放行外界取消，避免内部超时破坏已成功的 Cheap 请求。
            _logger.LogDebug(ex, "Cascade self-verify failed for {Cheap}, skipping upgrade", cheapModel.Name);
            return null;
        }
    }
}
