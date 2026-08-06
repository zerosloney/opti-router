using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class FailoverPolicyTests
{
    private static RouterDecision Apply(
        FailoverPolicy policy,
        RouterOptions options,
        IReadOnlyList<ModelEndpointOptions> candidates,
        IReadOnlySet<string>? failedModels = null)
    {
        failedModels ??= new HashSet<string>();
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "test")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0,
            FailedModels = failedModels
        };
        var decision = new RouterDecision
        {
            Candidates = candidates,
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        return policy.Apply(context, decision);
    }

    [Fact]
    public void Apply_NoFailedModels_KeepsCandidatesUnchanged()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new FailoverPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, candidates);

        Assert.Equal(candidates.Count, result.Candidates.Count);
        Assert.Contains("no-failed-models", result.Reason);
    }

    [Fact]
    public void Apply_FailedPrimary_RemovesFailedAndKeepsRemaining()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new FailoverPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var failed = new HashSet<string> { "gpt-4o" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
        Assert.Contains("removed failed", result.Reason);
    }

    [Fact]
    public void Apply_AllCandidatesFailed_FallsBackToCheapestAvailable()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m),
            ("small-model", ModelTier.Cheap, 8000, 0.005m));

        var policy = new FailoverPolicy();
        // Only include the two models that will fail in the previous decision
        var candidates = options.Models.Where(m => m.Enabled && m.Name != "small-model").ToList();
        var failed = new HashSet<string> { "gpt-4o", "deepseek-chat" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Single(result.Candidates);
        Assert.Equal("small-model", result.Candidates[0].Name);
        Assert.Contains("fallback", result.Reason);
    }

    [Fact]
    public void Apply_Disabled_KeepsCandidatesUnchanged()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new FailoverPolicy();
        options.Routing.EnableFailover = false;
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var failed = new HashSet<string> { "gpt-4o" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("disabled", result.Reason);
    }
}
