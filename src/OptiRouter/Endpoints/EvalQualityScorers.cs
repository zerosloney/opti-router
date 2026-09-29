using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 评测质量评分器实现与解析：
/// - <see cref="LlmJudgeEvalScorer"/>：参考答案引导的 LLM-as-judge，评测主链同步调用（离线评测无延迟约束），
///   复用线上 judge 的 JSON 契约与 <see cref="LlmQualityJudge.ParseScore"/> 容错解析；
/// - <see cref="EmbeddingEvalScorer"/>：向量引擎余弦相似度（默认 128 维词法特征哈希投影，配置 ONNX 后为真语义向量）。
/// 解析顺序：judge 已配置且可用 → judge；否则向量引擎；两者皆缺 → 运行器内置 token-jaccard 兜底。
/// </summary>
public static class EvalQualityScorers
{
    public static IEvalQualityScorer? Resolve(
        RoutingOptions routing,
        IOptionsMonitor<RouterOptions> options,
        IModelClientProvider clientProvider,
        ISemanticVectorEngine? vectorEngine)
    {
        var embedding = vectorEngine is not null ? new EmbeddingEvalScorer(vectorEngine) : null;
        if (!routing.EnableQualityJudge || string.IsNullOrWhiteSpace(routing.QualityJudgeModel))
            return embedding;

        var judgeModel = ModelDisplayIds.Resolve(
            options.CurrentValue.Models.Where(m => m.Enabled).ToList(),
            routing.QualityJudgeModel).FirstOrDefault();
        // 打分模型不可用（未解析/数据主权过滤）时退回向量口径，与线上 judge 的静默跳过语义对齐。
        if (judgeModel is null
            || (routing.EnableDataSovereignty && !DataSovereigntyPolicy.IsLocalOrPrivateCandidate(judgeModel)))
            return embedding;

        return new LlmJudgeEvalScorer(judgeModel, clientProvider);
    }
}

/// <summary>参考答案引导的 judge 评分器：问题 + 参考答案 + 模型回答 → JSON score ∈ [0,1]。</summary>
public sealed class LlmJudgeEvalScorer : IEvalQualityScorer
{
    // 与线上 LlmQualityJudge.DefaultJudgePrompt 同契约（score/reason JSON），但评测场景持有
    // ExpectedAnswer，改为参考答案引导评分：judge 对"语义等价、措辞不同"给高分，对编造/漏要点给低分。
    private const string JudgePrompt =
        "你是严格的回答质量评审员。下面是一道用户问题、参考答案与一个模型的回答。" +
        "评估模型回答与参考答案的语义一致性：关键事实、要点、结论是否一致，有无编造数据、引用或 API，是否切题。" +
        "措辞与参考答案不同不影响评分，语义等价即高分；遗漏参考答案的关键要点酌情扣分；" +
        "明显编造或答非所问 score ≤ 0.3。" +
        "只输出一个 JSON 对象：{\"score\": <0到1的小数，1=与参考答案语义一致且完整，0=完全错误或无关>, \"reason\": \"<一句话理由>\"}。" +
        "不要输出 JSON 之外的任何文字。";

    private static readonly TimeSpan JudgeTimeout = TimeSpan.FromSeconds(60);

    private readonly ModelEndpointOptions _judgeModel;
    private readonly IModelClientProvider _clientProvider;

    public LlmJudgeEvalScorer(ModelEndpointOptions judgeModel, IModelClientProvider clientProvider)
    {
        _judgeModel = judgeModel;
        _clientProvider = clientProvider;
    }

    /// <inheritdoc />
    public string Name => $"llm-judge:{_judgeModel.Name}";

    /// <inheritdoc />
    public async Task<double> ScoreAsync(EvalTestCase testCase, string actualAnswer, CancellationToken ct)
    {
        var judgeReq = new ChatRequest
        {
            Messages =
            {
                ChatMessage.FromText("user",
                    $"【用户问题】\n{Truncate(testCase.Question, LlmQualityJudge.MaxQuestionChars)}\n\n" +
                    $"【参考答案】\n{Truncate(testCase.ExpectedAnswer, LlmQualityJudge.MaxQuestionChars)}\n\n" +
                    $"【模型回答】\n{LlmQualityJudge.TruncateForJudge(actualAnswer)}\n\n{JudgePrompt}")
            }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(JudgeTimeout);
        var client = _clientProvider.GetClient(_judgeModel);
        var response = await client.CompleteRawAsync(judgeReq, cts.Token).ConfigureAwait(false);

        return LlmQualityJudge.ParseScore(response)
            ?? throw new InvalidOperationException($"judge 输出不含有效 score（model={_judgeModel.Name}）");
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars];
}

/// <summary>向量余弦评分器：Embed(参考答案) 与 Embed(模型回答) 的余弦相似度，负值截断为 0。</summary>
public sealed class EmbeddingEvalScorer : IEvalQualityScorer
{
    private readonly ISemanticVectorEngine _engine;

    public EmbeddingEvalScorer(ISemanticVectorEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public string Name => "embedding-cosine";

    /// <inheritdoc />
    public Task<double> ScoreAsync(EvalTestCase testCase, string actualAnswer, CancellationToken ct)
    {
        var expected = _engine.Embed(testCase.ExpectedAnswer);
        var actual = _engine.Embed(actualAnswer);
        double similarity = DenseEmbeddingVectorEngine.CosineSimilarity(expected, actual);
        // 余弦 [-1,1] 截断到 [0,1]：负相似度在评测阈值语义下应判 0 分而非负分（阈值 0.6 语义与
        // 语义缓存同族——0.6 以上的词法/语义重合才算"质量通过"）。
        return Task.FromResult(Math.Clamp(similarity, 0.0, 1.0));
    }
}
