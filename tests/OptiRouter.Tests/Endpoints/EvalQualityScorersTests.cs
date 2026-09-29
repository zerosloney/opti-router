using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 评测质量口径主链化回归：评分器解析顺序（judge → 向量 → jaccard 兜底）、
/// judge 评分器的 JSON 契约解析、向量余弦口径，以及 OfflineEvalRunner 的评分器接管
/// 与单例失败降级（QualityMetric 标注口径）。
/// </summary>
public sealed class EvalQualityScorersTests
{
    private static ModelEndpointOptions JudgeEndpoint() => new()
    {
        Name = "judge-model",
        BaseUrl = "http://localhost/v1",
        Tier = ModelTier.Strong,
        Enabled = true
    };

    private static RouterOptions Options(Action<RoutingOptions>? configure = null)
    {
        var options = new RouterOptions();
        options.Models.Add(JudgeEndpoint());
        configure?.Invoke(options.Routing);
        return options;
    }

    private static RawChatResponse JudgeResponse(string content) => new(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        }),
        new ChatUsage { PromptTokens = 10, CompletionTokens = 2, TotalTokens = 12 });

    // ---- EvalQualityScorers.Resolve 解析顺序 ----

    [Fact]
    public void Resolve_JudgeConfigured_ReturnsJudgeScorer()
    {
        var options = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "judge-model";
        });
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>
        {
            ["judge-model"] = new MockModelClient(JudgeEndpoint())
        });

        var scorer = EvalQualityScorers.Resolve(options.Routing, new FakeRouterOptionsMonitor(options), provider, vectorEngine: null);

        var judge = Assert.IsType<LlmJudgeEvalScorer>(scorer);
        Assert.Equal("llm-judge:judge-model", judge.Name);
    }

    [Fact]
    public void Resolve_JudgeDisabled_FallsBackToEmbedding()
    {
        var options = Options();
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>());

        var scorer = EvalQualityScorers.Resolve(
            options.Routing, new FakeRouterOptionsMonitor(options), provider, new DenseEmbeddingVectorEngine());

        Assert.IsType<EmbeddingEvalScorer>(scorer);
        Assert.Equal("embedding-cosine", scorer!.Name);
    }

    [Fact]
    public void Resolve_JudgeUnresolvable_FallsBackToEmbedding()
    {
        var options = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "no-such-model";
        });
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>());

        var scorer = EvalQualityScorers.Resolve(
            options.Routing, new FakeRouterOptionsMonitor(options), provider, new DenseEmbeddingVectorEngine());

        Assert.IsType<EmbeddingEvalScorer>(scorer);
    }

    [Fact]
    public void Resolve_NothingAvailable_ReturnsNull()
    {
        var options = Options();
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>());

        Assert.Null(EvalQualityScorers.Resolve(options.Routing, new FakeRouterOptionsMonitor(options), provider, vectorEngine: null));
    }

    // ---- LlmJudgeEvalScorer ----

    [Fact]
    public async Task LlmJudgeEvalScorer_ParsesJudgeScore()
    {
        var endpoint = JudgeEndpoint();
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>
        {
            ["judge-model"] = new MockModelClient(endpoint,
                completeRawFunc: (_, _) => Task.FromResult(JudgeResponse("{\"score\": 0.85, \"reason\": \"语义一致\"}")))
        });
        var scorer = new LlmJudgeEvalScorer(endpoint, provider);

        double score = await scorer.ScoreAsync(new EvalTestCase("t1", "什么是多态", "多态是同一接口的不同实现"), "多态指同一接口的不同实现", CancellationToken.None);

        Assert.Equal(0.85, score, precision: 6);
    }

    [Fact]
    public async Task LlmJudgeEvalScorer_UnparsableOutput_Throws()
    {
        var endpoint = JudgeEndpoint();
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>
        {
            ["judge-model"] = new MockModelClient(endpoint,
                completeRawFunc: (_, _) => Task.FromResult(JudgeResponse("我觉得挺好")))
        });
        var scorer = new LlmJudgeEvalScorer(endpoint, provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scorer.ScoreAsync(new EvalTestCase("t1", "q", "ref"), "answer", CancellationToken.None));
    }

    [Fact]
    public async Task LlmJudgeEvalScorer_PromptCarriesQuestionReferenceAndAnswer()
    {
        string? captured = null;
        var endpoint = JudgeEndpoint();
        var provider = new TestModelClientProvider(new Dictionary<string, IModelClient>
        {
            ["judge-model"] = new MockModelClient(endpoint,
                completeRawFunc: (request, _) =>
                {
                    captured = request.Messages![^1].GetText();
                    return Task.FromResult(JudgeResponse("{\"score\": 0.5, \"reason\": \"ok\"}"));
                })
        });
        var scorer = new LlmJudgeEvalScorer(endpoint, provider);

        await scorer.ScoreAsync(new EvalTestCase("t1", "用户问题原文", "参考答案原文"), "模型回答原文", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("【用户问题】", captured);
        Assert.Contains("用户问题原文", captured);
        Assert.Contains("【参考答案】", captured);
        Assert.Contains("参考答案原文", captured);
        Assert.Contains("【模型回答】", captured);
        Assert.Contains("模型回答原文", captured);
        Assert.Contains("score", captured);
    }

    // ---- EmbeddingEvalScorer ----

    [Fact]
    public async Task EmbeddingEvalScorer_IdenticalTextScoresOne()
    {
        var scorer = new EmbeddingEvalScorer(new DenseEmbeddingVectorEngine());
        var testCase = new EvalTestCase("t1", "q", "The capital of France is Paris");

        double score = await scorer.ScoreAsync(testCase, "The capital of France is Paris", CancellationToken.None);

        Assert.Equal(1.0, score, precision: 6);
    }

    // ---- OfflineEvalRunner × 评分器主链 ----

    private sealed class StubScorer : IEvalQualityScorer
    {
        public string Name => "stub";
        public int Calls;
        public Func<EvalTestCase, string, double> Score { get; set; } = (_, _) => 1.0;

        public Task<double> ScoreAsync(EvalTestCase testCase, string actualAnswer, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Score(testCase, actualAnswer));
        }
    }

    private sealed class ThrowingScorer : IEvalQualityScorer
    {
        public string Name => "throwing";
        public Task<double> ScoreAsync(EvalTestCase testCase, string actualAnswer, CancellationToken ct)
            => throw new InvalidOperationException("scorer down");
    }

    [Fact]
    public async Task RunBatchEval_ScorerOverridesJaccard_AndLabelsQualityMetric()
    {
        // 语义等价但词面完全不同：jaccard≈0（会被判失败），stub 评分 0.95（应判通过）。
        var stub = new StubScorer { Score = (_, _) => 0.95 };
        var dataset = new List<EvalTestCase>
        {
            new("tc-1", "q1", "correct answer")
        };

        var report = await OfflineEvalRunner.RunBatchEvalAsync(
            "batch-scorer",
            dataset,
            (_, _) => Task.FromResult(new RawChatResponse(
                "{\"choices\":[{\"message\":{\"content\":\"完全不同的表述\"}}]}",
                new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 })),
            similarityThreshold: 0.6,
            qualityScorer: stub);

        Assert.Equal(1, stub.Calls);
        Assert.True(report.Results[0].QualityPassed);
        Assert.Equal(0.95, report.Results[0].SimilarityScore, precision: 6);
        Assert.Equal("stub", report.Results[0].QualityMetric);
        Assert.Equal(1, report.QualityPassedCases);
    }

    [Fact]
    public async Task RunBatchEval_ScorerFailure_CountsAsZeroAndLabelsError()
    {
        var dataset = new List<EvalTestCase>
        {
            new("tc-1", "q1", "correct answer")
        };

        var report = await OfflineEvalRunner.RunBatchEvalAsync(
            "batch-error",
            dataset,
            (_, _) => Task.FromResult(new RawChatResponse(
                "{\"choices\":[{\"message\":{\"content\":\"correct answer\"}}]}",
                new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 })),
            similarityThreshold: 0.6,
            qualityScorer: new ThrowingScorer());

        // 评分器挂了：单例计 0 分不中断整批，口径标 ":error"，jaccard 兜底值不冒充评分。
        Assert.False(report.Results[0].QualityPassed);
        Assert.Equal(0.0, report.Results[0].SimilarityScore, precision: 6);
        Assert.Equal("throwing:error", report.Results[0].QualityMetric);
    }

    [Fact]
    public async Task RunBatchEval_WithoutScorer_KeepsJaccardFallback()
    {
        var dataset = new List<EvalTestCase>
        {
            new("tc-1", "q1", "correct answer")
        };

        var report = await OfflineEvalRunner.RunBatchEvalAsync(
            "batch-jaccard",
            dataset,
            (_, _) => Task.FromResult(new RawChatResponse(
                "{\"choices\":[{\"message\":{\"content\":\"correct answer\"}}]}",
                new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 })),
            similarityThreshold: 0.6);

        Assert.True(report.Results[0].QualityPassed);
        Assert.Equal("token-jaccard", report.Results[0].QualityMetric);
    }
}
