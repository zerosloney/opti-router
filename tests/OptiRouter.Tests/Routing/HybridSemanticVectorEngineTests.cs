using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class HybridSemanticVectorEngineTests
{
    private static RouterOptions BuildValidOptions()
    {
        var options = TestHelpers.BuildOptions(
            ("cheap-1", ModelTier.Cheap, 8192, 1m),
            ("medium-1", ModelTier.Medium, 16384, 5m),
            ("strong-1", ModelTier.Strong, 32768, 15m));

        foreach (var m in options.Models)
        {
            m.BaseUrl = "https://example.com";
        }

        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        options.Routing.HybridHighConfidenceThreshold = 0.45;
        options.Routing.SemanticRouterMode = "Hybrid";
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "explicit_code_route",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string> { "python script", "write sql query", "c# class" }
            },
            new()
            {
                Name = "implicit_reasoning_route",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string> { "帮我分析一下规则漏洞", "琢磨这个逻辑合理吗", "盘一下潜在风险" }
            }
        };

        return options;
    }

    [Fact]
    public void DenseEmbeddingVectorEngine_ComputesValidCosineSimilarity()
    {
        var engine = new DenseEmbeddingVectorEngine();
        var vec1 = engine.GetEmbedding("帮我分析一下规则漏洞");
        var vec2 = engine.GetEmbedding("帮我分析一下逻辑漏洞");
        var vec3 = engine.GetEmbedding("hello world english query");

        double sim12 = DenseEmbeddingVectorEngine.CosineSimilarity(vec1, vec2);
        double sim13 = DenseEmbeddingVectorEngine.CosineSimilarity(vec1, vec3);

        Assert.True(sim12 > 0, "Related Chinese reasoning phrases should have positive similarity");
        Assert.True(sim12 > sim13, "Related phrases should have higher similarity than unrelated English phrase");
    }

    [Fact]
    public void DefaultFeatureHash_IsStableFnv1aProjection()
    {
        const string token = "stabletoken";
        uint expectedHash = 2166136261;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(token))
        {
            expectedHash ^= b;
            expectedHash *= 16777619;
        }

        var vector = new DenseEmbeddingVectorEngine().GetEmbedding(token);
        int active = Array.FindIndex(vector, value => value > 0);

        Assert.Equal((int)(expectedHash % 128), active);
        Assert.Equal(1, vector.Count(value => value > 0));
    }

    [Fact]
    public void HybridEngine_HighConfidenceSparse_ShortCircuits()
    {
        var sparse = new TfIdfSemanticVectorEngine();
        var dense = new DenseEmbeddingVectorEngine();
        var hybrid = new HybridSemanticVectorEngine(sparse, dense, highConfidenceThreshold: 0.30);

        var routes = BuildValidOptions().Routing.SemanticRoutes;

        // 包含精准短语 "python script"
        var (matched, sim) = hybrid.Match("please write a python script", routes);

        Assert.NotNull(matched);
        Assert.Equal("explicit_code_route", matched.Name);
        Assert.True(sim >= 0.30);
    }

    [Fact]
    public void HybridEngine_GreyZoneSparse_TriggersDenseRerank()
    {
        var sparse = new TfIdfSemanticVectorEngine();
        var dense = new DenseEmbeddingVectorEngine();
        // 设置极高短路门槛，确保转入 Dense 重排
        var hybrid = new HybridSemanticVectorEngine(sparse, dense, highConfidenceThreshold: 0.99);

        var routes = BuildValidOptions().Routing.SemanticRoutes;

        // 口述型表达："帮我分析一下业务逻辑缺陷"
        var (matched, sim) = hybrid.Match("帮我分析一下业务逻辑缺陷", routes);

        Assert.NotNull(matched);
        Assert.Equal("implicit_reasoning_route", matched.Name);
        Assert.True(sim > 0);
    }

    [Fact]
    public void HybridEngine_LowConfidence_UsesSecondStageWithoutComparingForeignScores()
    {
        var sparseRoute = new SemanticRouteOptions { Name = "sparse", TargetTier = ModelTier.Cheap };
        var secondRoute = new SemanticRouteOptions { Name = "second", TargetTier = ModelTier.Strong };
        var hybrid = new HybridSemanticVectorEngine(
            new FixedEngine(sparseRoute, 0.40),
            new FixedEngine(secondRoute, 0.10),
            highConfidenceThreshold: 0.50);

        var (matched, score) = hybrid.Match("query", new List<SemanticRouteOptions> { sparseRoute, secondRoute });

        Assert.Same(secondRoute, matched);
        Assert.Equal(0.10, score, precision: 8);
    }

    private sealed class FixedEngine : ISemanticVectorEngine
    {
        private readonly SemanticRouteOptions? _route;
        private readonly double _score;

        public FixedEngine(SemanticRouteOptions? route, double score) => (_route, _score) = (route, score);

        public (SemanticRouteOptions? MatchedRoute, double MaxSimilarity) Match(
            string queryText,
            List<SemanticRouteOptions> routes) => (_route, _score);

        public float[] Embed(string text) => Array.Empty<float>();
    }

    [Fact]
    public void SemanticRouterPolicy_SupportsModeSwitching()
    {
        var options = BuildValidOptions();
        var policy = new SemanticRouterPolicy();

        var initialCandidates = options.Models.ToList();

        // 1. Hybrid Mode
        options.Routing.SemanticRouterMode = "Hybrid";
        var context = new RouterContext
        {
            Request = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", "帮我分析一下规则漏洞") }
            },
            AllModels = initialCandidates,
            Options = options
        };

        var initialDecision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = "initial"
        };

        var decision = policy.Apply(context, initialDecision);
        Assert.Contains("semantic-router: matched=implicit_reasoning_route", decision.Reason);

        // 2. TfIdf Mode
        options.Routing.SemanticRouterMode = "TfIdf";
        var decisionTfIdf = policy.Apply(context, initialDecision);
        Assert.NotNull(decisionTfIdf);

        // 3. Dense Mode
        options.Routing.SemanticRouterMode = "Dense";
        var decisionDense = policy.Apply(context, initialDecision);
        Assert.NotNull(decisionDense);
    }

    [Fact]
    public void Validator_ValidatesSemanticRouterModeAndThresholds()
    {
        var validator = new RouterOptionsValidator();
        var options = BuildValidOptions();

        // 无效模式
        options.Routing.SemanticRouterMode = "InvalidMode";
        var res1 = validator.Validate(null, options);
        Assert.True(res1.Failed);
        Assert.Contains("SemanticRouterMode", res1.FailureMessage);

        // 无效阈值
        options.Routing.SemanticRouterMode = "Hybrid";
        options.Routing.HybridHighConfidenceThreshold = 1.5;
        var res2 = validator.Validate(null, options);
        Assert.True(res2.Failed);
        Assert.Contains("HybridHighConfidenceThreshold", res2.FailureMessage);

        // 有效配置
        options.Routing.HybridHighConfidenceThreshold = 0.5;
        var res3 = validator.Validate(null, options);
        Assert.False(res3.Failed);
    }
}
