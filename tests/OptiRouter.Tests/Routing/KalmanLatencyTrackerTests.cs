using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class KalmanLatencyTrackerTests
{
    [Fact]
    public void RecordObservation_SmoothsNoisyLatencies_And_CalculatesP99()
    {
        var tracker = new KalmanLatencyTracker(processNoiseQ: 10.0, measurementNoiseR: 400.0, targetLatencyMs: 1000.0, penaltyGamma: 1.5);

        for (int i = 0; i < 10; i++)
        {
            tracker.RecordObservation("model-a", 400.0 + (i % 2 == 0 ? 20 : -20));
        }

        var estimate = tracker.GetEstimate("model-a");

        Assert.InRange(estimate.EstimatedLatencyMs, 350.0, 450.0);
        Assert.True(estimate.EstimatedP99Ms < 1000.0, $"P99 {estimate.EstimatedP99Ms} should be < 1000");
        Assert.Equal(1.0, estimate.PenaltyWeightFactor, 3);
    }

    [Fact]
    public void RecordObservation_HighTailLatency_TriggersPenaltyWeight()
    {
        var tracker = new KalmanLatencyTracker(processNoiseQ: 50.0, measurementNoiseR: 100.0, targetLatencyMs: 500.0, penaltyGamma: 2.0);

        for (int i = 0; i < 10; i++)
        {
            tracker.RecordObservation("lagging-model", 1200.0 + (i * 30));
        }

        var estimate = tracker.GetEstimate("lagging-model");

        Assert.True(estimate.EstimatedP99Ms > 500.0, "P99 should exceed 500ms target");
        Assert.True(estimate.PenaltyWeightFactor < 0.5, $"Penalty factor {estimate.PenaltyWeightFactor} should be significantly reduced");
    }

    [Fact]
    public void LoadBalancePolicy_SuppressesLaggingProvider()
    {
        var tracker = new KalmanLatencyTracker(targetLatencyMs: 500.0, penaltyGamma: 2.0);

        for (int i = 0; i < 10; i++) tracker.RecordObservation("fast-model", 300.0);
        for (int i = 0; i < 10; i++) tracker.RecordObservation("slow-model", 1200.0);

        var policy = new LoadBalancePolicy(tracker);

        var fastCandidate = new ModelEndpointOptions { Name = "fast-model", Tier = ModelTier.Medium, MaxContextTokens = 10000 };
        var slowCandidate = new ModelEndpointOptions { Name = "slow-model", Tier = ModelTier.Medium, MaxContextTokens = 10000 };
        var candidates = new List<ModelEndpointOptions> { fastCandidate, slowCandidate };

        var context = new RouterContext
        {
            Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") } },
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableLoadBalance = true,
                    EnableKalmanLoadBalance = true
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };

        int fastChosenFirst = 0;
        int trials = 100;

        for (int t = 0; t < trials; t++)
        {
            var result = policy.Apply(context, decision);
            if (result.Candidates.Count > 0 && result.Candidates[0].Name == "fast-model")
            {
                fastChosenFirst++;
            }
        }

        Assert.True(fastChosenFirst > 80, $"Fast model was chosen first {fastChosenFirst}/{trials} times, expected > 80 due to Kalman penalty");
    }
}
