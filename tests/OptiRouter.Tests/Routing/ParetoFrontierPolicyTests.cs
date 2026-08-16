using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ParetoFrontierPolicyTests
{
    private static List<ModelEndpointOptions> GetTestCandidates() => new()
    {
        // Pareto Optimal: High Quality, High Cost
        new ModelEndpointOptions { Name = "strong-pro", Tier = ModelTier.Strong, InputPricePerMillion = 10.0m, OutputPricePerMillion = 30.0m },
        // Pareto Optimal: Moderate Quality, Low Cost
        new ModelEndpointOptions { Name = "cheap-fast", Tier = ModelTier.Cheap, InputPricePerMillion = 0.5m, OutputPricePerMillion = 1.5m },
        // Pareto Dominated: Low Quality (Cheap), High Cost (15.0/M - worse cost & quality than cheap-fast!)
        new ModelEndpointOptions { Name = "wasteful-model", Tier = ModelTier.Cheap, InputPricePerMillion = 15.0m, OutputPricePerMillion = 45.0m },
    };

    [Fact]
    public void EvaluateCandidates_IdentifiesParetoDominatedModel()
    {
        var regulator = new ParetoFrontierRegulator();
        var candidates = GetTestCandidates();

        var evaluated = regulator.EvaluateCandidates(candidates, estimatedTokens: 1000, qualityWeight: 0.7);

        var wasteful = evaluated.First(c => c.Model.Name == "wasteful-model");
        var cheapFast = evaluated.First(c => c.Model.Name == "cheap-fast");
        var strongPro = evaluated.First(c => c.Model.Name == "strong-pro");

        Assert.True(wasteful.IsParetoDominated, "wasteful-model should be identified as Pareto-dominated");
        Assert.False(cheapFast.IsParetoDominated, "cheap-fast should be Pareto-optimal");
        Assert.False(strongPro.IsParetoDominated, "strong-pro should be Pareto-optimal");
    }

    [Fact]
    public void ParetoPolicy_QualityDominantWeight_PrefersStrongModel()
    {
        var policy = new ParetoFrontierPolicy();
        var candidates = GetTestCandidates();

        var context = new RouterContext
        {
            Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "test") } },
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableParetoFrontierRegulator = true,
                    ParetoQualityWeight = 0.9 // 90% Quality preference
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };
        var result = policy.Apply(context, decision);

        Assert.Equal("strong-pro", result.Candidates[0].Name);
    }

    [Fact]
    public void ParetoPolicy_CostDominantWeight_PrefersCheapModel()
    {
        var policy = new ParetoFrontierPolicy();
        var candidates = GetTestCandidates();

        var context = new RouterContext
        {
            Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "test") } },
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableParetoFrontierRegulator = true,
                    ParetoQualityWeight = 0.1 // 10% Quality preference (90% Cost focus)
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };
        var result = policy.Apply(context, decision);

        Assert.Equal("cheap-fast", result.Candidates[0].Name);
    }

    [Fact]
    public void ParetoPolicy_StrictFilter_RemovesDominatedModel()
    {
        var policy = new ParetoFrontierPolicy();
        var candidates = GetTestCandidates();

        var context = new RouterContext
        {
            Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "test") } },
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableParetoFrontierRegulator = true,
                    ParetoQualityWeight = 0.5,
                    ParetoStrictFrontierFilter = true // Strictly remove dominated models
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };
        var result = policy.Apply(context, decision);

        Assert.DoesNotContain(result.Candidates, m => m.Name == "wasteful-model");
        Assert.Equal(2, result.Candidates.Count);
    }
}
