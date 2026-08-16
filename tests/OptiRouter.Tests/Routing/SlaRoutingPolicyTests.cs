using OptiRouter.Configuration;
using OptiRouter.Routing;
using OptiRouter.Clients;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class SlaRoutingPolicyTests
{
    private sealed class TestLatencyStatsProvider : ILatencyStatsProvider
    {
        private readonly Dictionary<string, ModelLatencyStats> _dict = new();

        public void Add(string model, ModelLatencyStats stats) => _dict[model] = stats;

        public ModelLatencyStats? GetStats(string modelName) => _dict.TryGetValue(modelName, out var s) ? s : null;

        public void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats) { }
    }

    [Fact]
    public void LatencyAwarePolicy_TtftSla_PrioritizesFastestFirstTokenModel()
    {
        var statsProvider = new TestLatencyStatsProvider();
        // Model A: Total latency 1000ms, TTFT 50ms
        statsProvider.Add("model-a", new ModelLatencyStats(1000, 1200, 10, AverageTtftMs: 50, AverageTps: 20));
        // Model B: Total latency 500ms, TTFT 200ms
        statsProvider.Add("model-b", new ModelLatencyStats(500, 600, 10, AverageTtftMs: 200, AverageTps: 50));

        var policy = new LatencyAwarePolicy(statsProvider, new ThompsonStateStore());

        var context = new RouterContext
        {
            Request = new Clients.ChatRequest { Messages = [ChatMessage.FromText("user", "hi")] },
            AllModels = [],
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableLatencyAware = true,
                    DefaultSlaMode = SlaMode.Ttft
                }
            }
        };

        var initialCandidates = new List<ModelEndpointOptions>
        {
            new ModelEndpointOptions { Name = "model-b", Tier = ModelTier.Cheap },
            new ModelEndpointOptions { Name = "model-a", Tier = ModelTier.Cheap }
        };

        var decision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = "initial"
        };

        var result = policy.Apply(context, decision);

        Assert.Equal("model-a", result.Candidates[0].Name);
        Assert.Equal("model-b", result.Candidates[1].Name);
    }

    [Fact]
    public void LatencyAwarePolicy_TpsSla_PrioritizesHighestThroughputModel()
    {
        var statsProvider = new TestLatencyStatsProvider();
        // Model A: Total latency 1000ms, TPS 100
        statsProvider.Add("model-a", new ModelLatencyStats(1000, 1200, 10, AverageTtftMs: 200, AverageTps: 100));
        // Model B: Total latency 500ms, TPS 20
        statsProvider.Add("model-b", new ModelLatencyStats(500, 600, 10, AverageTtftMs: 50, AverageTps: 20));

        var policy = new LatencyAwarePolicy(statsProvider, new ThompsonStateStore());

        var context = new RouterContext
        {
            Request = new Clients.ChatRequest { Messages = [ChatMessage.FromText("user", "hi")] },
            AllModels = [],
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableLatencyAware = true,
                    DefaultSlaMode = SlaMode.Tps
                }
            }
        };

        var initialCandidates = new List<ModelEndpointOptions>
        {
            new ModelEndpointOptions { Name = "model-b", Tier = ModelTier.Cheap },
            new ModelEndpointOptions { Name = "model-a", Tier = ModelTier.Cheap }
        };

        var decision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = "initial"
        };

        var result = policy.Apply(context, decision);

        Assert.Equal("model-a", result.Candidates[0].Name);
        Assert.Equal("model-b", result.Candidates[1].Name);
    }
}
