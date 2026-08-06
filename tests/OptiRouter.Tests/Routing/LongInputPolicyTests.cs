using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class LongInputPolicyTests
{
    private static RouterDecision Apply(LongInputPolicy policy, RouterOptions options, int estimatedTokens, params ModelEndpointOptions[] candidates)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", new string('x', 100))),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = estimatedTokens,
            FailedModels = new HashSet<string>()
        };
        var decision = new RouterDecision
        {
            Candidates = candidates.ToList(),
            Reason = "initial",
            EstimatedInputTokens = estimatedTokens
        };
        return policy.Apply(context, decision);
    }

    [Fact]
    public void Apply_InputWithinThreshold_KeepsCandidatesUnchanged()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 1000, candidates.ToArray());

        Assert.Equal(candidates.Count, result.Candidates.Count);
        Assert.Contains("within-threshold", result.Reason);
    }

    [Fact]
    public void Apply_InputExceedsThreshold_FiltersToSufficientContextModels()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("small-model", ModelTier.Cheap, 8000, 0.005m));

        var policy = new LongInputPolicy();
        // Default threshold is 32000; set estTokens > 32000
        // 32000 * 1.2 = 38400 required context
        int required = (int)Math.Ceiling(40000 * 1.2); // 48000
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 40000, candidates.ToArray());

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
        Assert.Contains("long-input: filtered", result.Reason);
    }

    [Fact]
    public void Apply_NoModelCanFit_KeepsOriginalCandidatesWithWarning()
    {
        var options = TestHelpers.BuildOptions(
            ("small-model", ModelTier.Cheap, 8000, 0.005m));

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 40000, candidates.ToArray());

        Assert.Single(result.Candidates);
        Assert.Equal("small-model", result.Candidates[0].Name);
        Assert.Contains("no model fits", result.Reason);
    }
}
