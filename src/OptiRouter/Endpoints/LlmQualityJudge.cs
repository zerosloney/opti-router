using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// LLM-as-judge 采样质量打分：按采样率把成功响应（问题 + 回答原文）发给打分模型，
/// 解析 JSON score ∈ [0,1] 后经显式质量入口回灌 Thompson/LinUCB——语义级质量信号，
/// 补启发式规则（截断/空答/JSON 违约）覆盖不到的"答得对不对"盲区。
/// 完全旁路：后台 fire-and-forget，任何异常只留日志，不影响请求主流程；
/// 打分调用真实计费并记审计（reason 含 llm-judge、fusionRole=judge），但不上报断路器
/// （评审任务超时不代表该模型服务能力故障）。成本上界 = 采样率 × 截断后 prompt。
/// </summary>
public sealed class LlmQualityJudge
{
    /// <summary>judge prompt 常量。强制 JSON 契约：score 数值 ∈ [0,1]，reason 一句话。</summary>
    public const string DefaultJudgePrompt =
        "你是严格的回答质量评审员。下面是一道用户问题与一个模型的回答。请评估回答的正确性、完整性与相关性，" +
        "只输出一个 JSON 对象：{\"score\": <0到1的小数，1=完全正确且切题，0=完全错误或无关>, \"reason\": \"<一句话理由>\"}。" +
        "不要输出 JSON 之外的任何文字。";

    internal const int MaxQuestionChars = 4000;
    internal const int MaxAnswerChars = 8000;
    private static readonly TimeSpan JudgeTimeout = TimeSpan.FromSeconds(60);
    private const int MaxConcurrentJudges = 4;
    // intentional-simple: fixed process-wide cap of 4; upgrade to a configurable bounded queue if judge throughput needs to grow.
    private static readonly SemaphoreSlim JudgeConcurrency = new(MaxConcurrentJudges, MaxConcurrentJudges);

    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly OutcomeRecorder _recorder;
    private readonly IModelClientProvider _clientProvider;
    private readonly ILogger<LlmQualityJudge> _logger;

    public LlmQualityJudge(
        IOptionsMonitor<RouterOptions> options,
        OutcomeRecorder recorder,
        IModelClientProvider clientProvider,
        ILogger<LlmQualityJudge> logger)
    {
        _options = options;
        _recorder = recorder;
        _clientProvider = clientProvider;
        _logger = logger;
    }

    /// <summary>
    /// 同步派发：开关/采样/解析判定在调用线程完成（零抛出），judge 上游调用转后台。
    /// </summary>
    /// <param name="originalRequest">用户原始请求（脱敏/压缩后的实际上游视角），用于提取问题文本。</param>
    /// <param name="answerText">被判模型的回答全文。</param>
    /// <param name="judgedModelName">被判模型名（回灌目标）。</param>
    /// <param name="decision">主路由决策——回灌复用它保证特征与决策时一致。</param>
    /// <param name="routedTier">审计行的档位上下文。</param>
    /// <param name="sessionId">会话标识（审计归属）。</param>
    public void TryJudge(ChatRequest originalRequest, string answerText, string judgedModelName,
        RouterDecision decision, ModelTier routedTier, string? sessionId)
    {
        try
        {
            var routing = _options.CurrentValue.Routing;
            if (!routing.EnableQualityJudge || string.IsNullOrWhiteSpace(answerText))
                return;
            if (string.IsNullOrWhiteSpace(routing.QualityJudgeModel))
                return;
            // rate>=1 恒送审；否则掷点。Random.Shared 线程安全度满足采样精度要求。
            if (routing.QualityJudgeSampleRate < 1.0 && Random.Shared.NextDouble() >= routing.QualityJudgeSampleRate)
                return;

            var judgeModel = ModelDisplayIds.Resolve(
                _options.CurrentValue.Models.Where(m => m.Enabled).ToList(),
                routing.QualityJudgeModel).FirstOrDefault();
            if (judgeModel is null)
            {
                _logger.LogDebug("Quality judge model '{Model}' not resolved, sampling skipped", routing.QualityJudgeModel);
                return;
            }
            if (routing.EnableDataSovereignty && !DataSovereigntyPolicy.IsLocalOrPrivateCandidate(judgeModel))
            {
                _logger.LogDebug("Quality judge model '{Model}' skipped by data sovereignty", judgeModel.Name);
                return;
            }
            // 自评偏置：judge 与被评模型同源时得分无信息量，跳过。
            if (judgeModel.Name.Equals(judgedModelName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!JudgeConcurrency.Wait(0))
            {
                _logger.LogDebug("Quality judge concurrency limit ({Limit}) reached, sampling skipped for '{Model}'",
                    MaxConcurrentJudges, judgeModel.Name);
                return;
            }

            // 后台执行：不 await，异常自兜底；TimeoutToken 防慢 judge 无界占用。
            var judgeModelCaptured = judgeModel;
            var permitTransferred = false;
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await JudgeCoreAsync(originalRequest, answerText, judgedModelName, judgeModelCaptured,
                            decision, routedTier, sessionId).ConfigureAwait(false);
                    }
                    finally
                    {
                        JudgeConcurrency.Release();
                    }
                });
                permitTransferred = true;
            }
            catch
            {
                if (!permitTransferred)
                    JudgeConcurrency.Release();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Quality judge dispatch skipped due to error");
        }
    }

    private async Task JudgeCoreAsync(ChatRequest originalRequest, string answerText, string judgedModelName,
        ModelEndpointOptions judgeModel, RouterDecision decision, ModelTier routedTier, string? sessionId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(JudgeTimeout);
        try
        {
            // 问题/回答双截断限定成本上界；BuildAnalystRequest 复用融合 analyst 的组装与寻址约定。
            var judgeReq = FusionSynthesis.BuildAnalystRequest(
                TruncateQuestion(originalRequest),
                new[] { (judgedModelName, TruncateForJudge(answerText)) },
                DefaultJudgePrompt,
                temperature: 0.0,
                requestJsonFormat: true);

            var client = _clientProvider.GetClient(judgeModel);
            var response = await client.CompleteRawAsync(judgeReq, cts.Token).ConfigureAwait(false);
            sw.Stop();

            int estimatedTokens = TokenEstimator.Estimate(judgeReq);
            decimal cost = response.Usage is not null
                ? CostCalculator.Compute(response.Usage, judgeModel)
                : OutcomeRecorder.EstimateInputCost(judgeModel, estimatedTokens);
            bool isEstimated = response.Usage is null;
            if (cost > 0m)
                _recorder.RecordCost(cost, sessionId);
            _recorder.RecordQuota(judgeModel.Name, response.Metadata);

            double? score = ParseScore(response);
            _recorder.RecordAudit(null, judgeModel.Name, estimatedTokens, response.Usage, cost,
                sw.ElapsedMilliseconds, sessionId, decision.Reason + "; llm-judge",
                true, null, false, routedTier, isAdopted: false, fusionRole: "judge",
                isEstimated: isEstimated,
                reward: null, epsilonPromotedModel: decision.EpsilonPromotedModel,
                requestContent: null, classificationSignal: decision.ClassificationSignal);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("LLM judge call completed: judge={Judge}, target={Target}, latency={Ms}ms",
                    judgeModel.Name, judgedModelName, sw.ElapsedMilliseconds);

            if (score is null)
            {
                // 契约违约不计分也不惩罚被评模型——只有 judge 掉链子，reward 维持延迟路径结果。
                _logger.LogDebug("LLM judge output unparsable for target {Target}, score skipped", judgedModelName);
                return;
            }
            double appliedReward = _recorder.RecordQualityOutcome(judgedModelName, score.Value, decision);
            _logger.LogInformation(
                "LLM judge scored {Target}: {Score:F2} (applied reward {Reward:F3}, judge={Judge})",
                judgedModelName, score.Value, appliedReward, judgeModel.Name);
        }
        catch (Exception ex)
        {
            sw.Stop();
            bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(ex);
            _recorder.RecordAudit(null, judgeModel.Name, TokenEstimator.Estimate(originalRequest), null, 0m,
                sw.ElapsedMilliseconds, sessionId, decision.Reason + "; llm-judge failed",
                false, UpstreamFailureClassifier.SafeMessage(ex, quotaLimited), false, routedTier,
                fusionRole: "judge", quotaLimited: quotaLimited,
                epsilonPromotedModel: decision.EpsilonPromotedModel, classificationSignal: decision.ClassificationSignal);
            _logger.LogDebug(ex, "LLM judge call failed for target {Target}", judgedModelName);
        }
    }

    /// <summary>截断最后一个 user 消息，限制重发问题的 token 成本上界。</summary>
    internal static ChatRequest TruncateQuestion(ChatRequest request)
    {
        var messages = new List<ChatMessage>(request.Messages);
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                string text = messages[i].GetText();
                if (text.Length > MaxQuestionChars)
                    messages[i] = ChatMessage.FromText("user", text[..MaxQuestionChars]);
                break;
            }
        }
        return request with { Messages = messages };
    }

    internal static string TruncateForJudge(string text) => text.Length <= MaxAnswerChars ? text : text[..MaxAnswerChars];

    /// <summary>从 judge 输出解析 score：经 JSON AST 修复容错围栏/闲聊，缺 score 字段视为无效。</summary>
    internal static double? ParseScore(RawChatResponse response)
    {
        string text = ResponseConfidenceChecker.ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            string json = JsonAstRepairer.RepairJson(text);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("score", out var scoreEl))
                return null;
            double score = scoreEl.ValueKind switch
            {
                JsonValueKind.Number => scoreEl.GetDouble(),
                JsonValueKind.String => double.TryParse(scoreEl.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s) ? s : double.NaN,
                _ => double.NaN
            };
            if (double.IsNaN(score))
                return null;
            return Math.Clamp(score, 0.0, 1.0);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
