using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class CrossProviderSpeculativeEngineTests
{
    private static List<ModelEndpointOptions> GetTestModels() => new()
    {
        new ModelEndpointOptions { Name = "claude-3-5-sonnet", Tier = ModelTier.Strong, Enabled = true },
        new ModelEndpointOptions { Name = "deepseek-v3-fast", Tier = ModelTier.Cheap, Enabled = true }
    };

    [Fact]
    public void BuildSpeculativePlan_FormsDraftAndTargetPair()
    {
        var engine = new CrossProviderSpeculativeEngine();
        var models = GetTestModels();
        var options = new RouterOptions
        {
            Routing = new RoutingOptions
            {
                EnableCrossProviderSpeculation = true,
                SpeculativeDraftTier = ModelTier.Cheap,
                SpeculativeTargetTier = ModelTier.Strong,
                SpeculativeDraftMaxTokens = 256
            }
        };

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Write an optimized LRU cache in C#.") }
        };

        var plan = engine.BuildSpeculativePlan(req, models, options);

        Assert.True(plan.IsSpeculationEligible);
        Assert.NotNull(plan.DraftModel);
        Assert.Equal("deepseek-v3-fast", plan.DraftModel.Name);
        Assert.NotNull(plan.TargetModel);
        Assert.Equal("claude-3-5-sonnet", plan.TargetModel.Name);
        Assert.Equal(256, plan.DraftMaxTokens);
        Assert.True(plan.ExpectedSpeedupRatio >= 1.5);
    }

    [Fact]
    public void BuildSpeculativePlan_Disabled_ReturnsIneligible()
    {
        var engine = new CrossProviderSpeculativeEngine();
        var models = GetTestModels();
        var options = new RouterOptions
        {
            Routing = new RoutingOptions
            {
                EnableCrossProviderSpeculation = false
            }
        };

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hello") }
        };

        var plan = engine.BuildSpeculativePlan(req, models, options);

        Assert.False(plan.IsSpeculationEligible);
        Assert.Null(plan.DraftModel);
        Assert.Null(plan.TargetModel);
    }
}
