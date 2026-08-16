using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class PredictiveResilienceEngineTests
{
    [Fact]
    public void RecordObservation_And_PredictCongestionRisk_CalculatesTemporalRisk()
    {
        var engine = new PredictiveResilienceEngine();
        var now = DateTimeOffset.UtcNow;
        int targetMinute = (now.Minute + 2) % 60;
        var targetTimestamp = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, targetMinute, 0, TimeSpan.Zero);

        // Simulate high failures for flaky-provider at target minute
        for (int i = 0; i < 10; i++)
        {
            engine.RecordObservation("flaky-provider", success: false, latencyMs: 5000, timestamp: targetTimestamp);
        }

        // Simulate success for stable-provider at target minute
        for (int i = 0; i < 10; i++)
        {
            engine.RecordObservation("stable-provider", success: true, latencyMs: 300, timestamp: targetTimestamp);
        }

        // Predict risk looking 2 minutes ahead from current minute
        double flakyRisk = engine.PredictCongestionRisk("flaky-provider", lookaheadMinutes: 2);
        double stableRisk = engine.PredictCongestionRisk("stable-provider", lookaheadMinutes: 2);

        Assert.True(flakyRisk > 0.50, $"Flaky provider risk {flakyRisk} should be > 0.50");
        Assert.True(stableRisk < 0.20, $"Stable provider risk {stableRisk} should be < 0.20");
    }

    [Fact]
    public void PredictiveResiliencePolicy_ReordersCandidatesAwayFromHighRisk()
    {
        var engine = new PredictiveResilienceEngine();
        int targetMinute = (DateTimeOffset.UtcNow.Minute + 2) % 60;
        var now = DateTimeOffset.UtcNow;
        var simulatedTimestamp = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, targetMinute, 0, TimeSpan.Zero);

        // Record high congestion on modelA at the target minute
        for (int i = 0; i < 10; i++)
        {
            engine.RecordObservation("modelA", success: false, latencyMs: 6000, timestamp: simulatedTimestamp);
            engine.RecordObservation("modelB", success: true, latencyMs: 200, timestamp: simulatedTimestamp);
        }

        var policy = new PredictiveResiliencePolicy(engine);

        var candA = new ModelEndpointOptions { Name = "modelA", Tier = ModelTier.Strong, Enabled = true };
        var candB = new ModelEndpointOptions { Name = "modelB", Tier = ModelTier.Strong, Enabled = true };

        var candidates = new List<ModelEndpointOptions> { candA, candB };

        var context = new RouterContext
        {
            Request = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", "test") }
            },
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnablePredictiveResilience = true,
                    PredictiveLookaheadMinutes = 2
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };
        var result = policy.Apply(context, decision);

        // modelB should now be top candidate due to lower temporal risk
        Assert.Equal("modelB", result.Candidates[0].Name);
        Assert.Contains("reordered by temporal safety", result.Reason);
    }
}
