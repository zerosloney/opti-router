using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ExplicitModelPolicyTests
{
    private static List<ModelEndpointOptions> GetModels() => new()
    {
        new() { Name = "strong-a", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
        new() { Name = "medium-a", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
        new() { Name = "cheap-a", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 32000 },
    };

    private static RouterContext Context(string? model, IEnumerable<ModelEndpointOptions>? models = null) => new()
    {
        Request = new ChatRequest
        {
            Model = model ?? string.Empty,
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") }
        },
        AllModels = (models ?? GetModels()).ToList(),
        Options = new RouterOptions(),
        FailedModels = new HashSet<string>()
    };

    private static RouterDecision Previous() => new()
    {
        Candidates = GetModels(),
        Reason = "init"
    };

    [Fact]
    public void Group_IsFilter()
    {
        Assert.Equal(PolicyGroup.Filter, new ExplicitModelPolicy().Group);
    }

    [Fact]
    public void ExplicitName_PinsToSingleCandidate()
    {
        var result = new ExplicitModelPolicy().Apply(Context("medium-a"), Previous());

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("medium-a", candidate.Name);
        Assert.Contains("pinned to 'medium-a'", result.Reason);
    }

    [Fact]
    public void ExplicitName_MatchIsCaseInsensitive()
    {
        var result = new ExplicitModelPolicy().Apply(Context("MEDIUM-A"), Previous());

        Assert.Equal("medium-a", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void AutoAlias_Passthrough()
    {
        var result = new ExplicitModelPolicy().Apply(Context("auto"), Previous());

        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains("auto alias", result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void AutoSemantics_Passthrough(string? model)
    {
        var result = new ExplicitModelPolicy().Apply(Context(model), Previous());

        Assert.Equal(3, result.Candidates.Count);
        Assert.True(ExplicitModelPolicy.IsAutoRouting(model));
    }

    [Fact]
    public void ConfiguredModelNamedAuto_WinsOverAlias()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "auto", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
            new() { Name = "other", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 }
        };

        var result = new ExplicitModelPolicy().Apply(Context("auto", models), Previous());

        Assert.Equal("auto", Assert.Single(result.Candidates).Name);
        Assert.Contains("pinned", result.Reason);
    }

    [Fact]
    public void UnknownModel_DefensivePassthrough()
    {
        var result = new ExplicitModelPolicy().Apply(Context("no-such-model"), Previous());

        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains("unknown model 'no-such-model'", result.Reason);
    }

    [Fact]
    public void UpstreamIdMatch_Unique_PinsToThatEndpoint()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "deepseek/deepseek-chat", Id = "deepseek-chat", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
            new() { Name = "gpt-4o", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 }
        };

        var result = new ExplicitModelPolicy().Apply(Context("deepseek-chat", models), Previous());

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("deepseek/deepseek-chat", candidate.Name);
    }

    [Fact]
    public void UpstreamIdMatch_MultipleProviders_PinsToAllEndpointsOfferingIt()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "deepseek/deepseek-chat", Id = "deepseek-chat", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
            new() { Name = "siliconflow/deepseek-chat", Id = "deepseek-chat", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 32000 },
            new() { Name = "gpt-4o", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 }
        };

        var result = new ExplicitModelPolicy().Apply(Context("deepseek-chat", models), Previous());

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, m => Assert.Equal("deepseek-chat", m.Id));
        Assert.Contains("2 endpoints offering 'deepseek-chat'", result.Reason);
    }

    [Fact]
    public void NameMatch_TakesPrecedenceOverIdMatch()
    {
        var models = new List<ModelEndpointOptions>
        {
            // 显式路由名恰好等于另一端点的上游 Id：按 Name 精确命中第一个端点。
            new() { Name = "deepseek-chat", Id = "other", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
            new() { Name = "mirror/deepseek-chat", Id = "deepseek-chat", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 32000 }
        };

        var result = new ExplicitModelPolicy().Apply(Context("deepseek-chat", models), Previous());

        Assert.Equal("deepseek-chat", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void EngineChain_ExplicitPin_SurvivesClassifierTierMismatch()
    {
        // 显式固定 cheap 模型 + 代码请求（分类器会选 Strong tier）：
        // 分类器在单元素池上筛 Strong 为空 → 回落 DefaultTier 仍为空 → 保留原候选。
        var options = TestHelpers.BuildOptions(
            ("strong-a", ModelTier.Strong, 128000, 10m),
            ("cheap-a", ModelTier.Cheap, 32000, 1m));
        var engine = new RouterEngine(
            new CostLedger(),
            new IRouterPolicy[] { new ExplicitModelPolicy(), new RuleClassifierPolicy() });

        var request = TestHelpers.BuildRequest(("user", "写一个二分查找的 Python 实现"));
        request = request with { Model = "cheap-a" };

        var decision = engine.Decide(request, options);

        Assert.Equal("cheap-a", Assert.Single(decision.Candidates).Name);
    }
}
