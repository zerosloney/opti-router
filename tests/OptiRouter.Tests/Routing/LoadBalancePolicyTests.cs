using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class LoadBalancePolicyTests
{
    private static List<ModelEndpointOptions> GetModels() => new()
    {
        new() { Name = "strong-a", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
        new() { Name = "medium-a", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
        new() { Name = "medium-b", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 32000 },
        new() { Name = "cheap-a", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 16000 },
    };

    private static RouterContext Context(bool enabled = true) => new()
    {
        Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") } },
        AllModels = GetModels(),
        Options = new RouterOptions { Routing = { EnableLoadBalance = enabled } }
    };

    [Fact]
    public void LoadBalance_Disabled_Passthrough()
    {
        var policy = new LoadBalancePolicy();
        var candidates = new List<ModelEndpointOptions> { GetModels()[0] };

        var result = policy.Apply(Context(enabled: false),
            new RouterDecision { Candidates = candidates, Reason = "init" });

        Assert.Contains("disabled", result.Reason);
        Assert.Same(candidates, result.Candidates);
    }

    [Fact]
    public void LoadBalance_SingleCandidate_Passthrough()
    {
        var policy = new LoadBalancePolicy();
        var candidates = new List<ModelEndpointOptions> { GetModels()[0] };

        var result = policy.Apply(Context(),
            new RouterDecision { Candidates = candidates, Reason = "init" });

        Assert.Contains("<2 candidates", result.Reason);
    }

    [Fact]
    public void LoadBalance_PreservesTierOrder()
    {
        var policy = new LoadBalancePolicy();
        var models = GetModels();
        // 输入：strong-a, medium-a, medium-b, cheap-a（tier 升序）
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { models[0], models[1], models[2], models[3] },
            Reason = "init"
        };

        var result = policy.Apply(Context(), previous);

        // tier 序列必须保持 Strong, Medium, Medium, Cheap（不跨 tier 打乱）
        var tiers = result.Candidates.Select(m => m.Tier).ToList();
        Assert.Equal(new[] { ModelTier.Strong, ModelTier.Medium, ModelTier.Medium, ModelTier.Cheap }, tiers);
    }

    [Fact]
    public void LoadBalance_DistributesWithinTier()
    {
        var policy = new LoadBalancePolicy();
        var models = GetModels();
        // medium 段有 2 个模型（medium-a 64K, medium-b 32K），权重比 2:1
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { models[1], models[2] },
            Reason = "init"
        };

        // 跑 100 次，统计 medium-a 出现在首位的次数。
        // 权重 2:1 → 期望约 67 次；放宽到 [40, 90] 容忍随机波动，同时排除"恒定选一个"的 bug。
        int aFirst = 0;
        for (int i = 0; i < 100; i++)
        {
            var result = policy.Apply(Context(), previous);
            if (result.Candidates[0].Name == "medium-a") aFirst++;
        }

        Assert.InRange(aFirst, 40, 90);
    }

    [Fact]
    public void LoadBalance_AllSingleTierSegments_NoReorder()
    {
        var policy = new LoadBalancePolicy();
        var models = GetModels();
        // 每个 tier 只 1 个 → 无可重排段
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { models[0], models[1], models[3] },
            Reason = "init"
        };

        var result = policy.Apply(Context(), previous);

        Assert.Contains("no change after shuffle", result.Reason);
        Assert.Equal(3, result.Candidates.Count);
    }
}
