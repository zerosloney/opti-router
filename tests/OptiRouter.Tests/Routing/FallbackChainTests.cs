using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 显式 fallback 链测试：模型配 FallbackChain 时优先用显式链，未配走自动 tier；链校验防自身/重复/悬空引用。
/// </summary>
public class FallbackChainTests
{
    private static ModelEndpointOptions Model(string name, ModelTier tier, params string[] fallback) => new()
    {
        Name = name,
        BaseUrl = "https://example.com",
        ApiKey = "k",
        Tier = tier,
        MaxContextTokens = 8192,
        Enabled = true,
        FallbackChain = fallback.ToList()
    };

    private static RouterContext Context(List<ModelEndpointOptions> models, HashSet<string> failed)
    {
        var options = new RouterOptions();
        options.Models.Clear();
        foreach (var m in models) options.Models.Add(m);
        options.Routing.EnableFailover = true;
        return new RouterContext
        {
            Request = new ChatRequest(),
            AllModels = models,
            Options = options,
            EstimatedInputTokens = 100,
            FailedModels = failed,
            SessionId = null
        };
    }

    [Fact]
    public void ExplicitChain_Used_WhenPrimaryFails()
    {
        var a = Model("A", ModelTier.Strong, "B", "C");
        var b = Model("B", ModelTier.Medium);
        var c = Model("C", ModelTier.Cheap);
        var models = new List<ModelEndpointOptions> { a, b, c };
        var policy = new FailoverPolicy(new ModelHealthTracker(() => DateTime.UtcNow));
        var decision = new RouterDecision { Candidates = new List<ModelEndpointOptions> { a }, Reason = "test" };

        var result = policy.Apply(Context(models, new HashSet<string> { "A" }), decision);

        Assert.Equal(new[] { "B", "C" }, result.Candidates.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void Failed_ChainMember_Skipped()
    {
        var a = Model("A", ModelTier.Strong, "B", "C");
        var b = Model("B", ModelTier.Medium);
        var c = Model("C", ModelTier.Cheap);
        var models = new List<ModelEndpointOptions> { a, b, c };
        var policy = new FailoverPolicy(new ModelHealthTracker(() => DateTime.UtcNow));
        var decision = new RouterDecision { Candidates = new List<ModelEndpointOptions> { a }, Reason = "test" };

        // A、B 都失败 → 链 [B,C] 中 B 被排除，剩 [C]
        var result = policy.Apply(Context(models, new HashSet<string> { "A", "B" }), decision);

        Assert.Equal(new[] { "C" }, result.Candidates.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void NoChain_FallsBackTo_AutoTier()
    {
        var a = Model("A", ModelTier.Strong); // 无显式链
        var b = Model("B", ModelTier.Medium);
        var models = new List<ModelEndpointOptions> { a, b };
        var policy = new FailoverPolicy(new ModelHealthTracker(() => DateTime.UtcNow));
        var decision = new RouterDecision { Candidates = new List<ModelEndpointOptions> { a }, Reason = "test" };

        var result = policy.Apply(Context(models, new HashSet<string> { "A" }), decision);

        // 无显式链 → 自动 tier：A(Strong) 失败 → Medium [B]
        Assert.Contains("B", result.Candidates.Select(m => m.Name));
    }

    [Fact]
    public void Validator_SelfReference_Fails()
    {
        var err = RouterOptionsValidator.ValidateModel(Model("A", ModelTier.Strong, "A"));
        Assert.NotNull(err);
        Assert.Contains("不能包含自身", err);
    }

    [Fact]
    public void Validator_Duplicate_Fails()
    {
        var err = RouterOptionsValidator.ValidateModel(Model("A", ModelTier.Strong, "B", "B"));
        Assert.NotNull(err);
        Assert.Contains("重复", err);
    }

    [Fact]
    public void Validator_MissingReference_Fails()
    {
        var options = new RouterOptions();
        options.Models.Clear();
        options.Models.Add(Model("A", ModelTier.Strong, "NotExist"));
        var result = new RouterOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("FallbackChain 引用了不存在的模型", result.FailureMessage);
    }
}
