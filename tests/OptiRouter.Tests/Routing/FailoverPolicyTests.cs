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

    private static FailoverPolicy NewPolicy(ModelHealthTracker? tracker = null)
        => new(tracker ?? new ModelHealthTracker());

    [Fact]
    public void Apply_NoFailedModels_KeepsCandidatesUnchanged()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = NewPolicy();
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

        var policy = NewPolicy();
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

        var policy = NewPolicy();
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

        var policy = NewPolicy();
        options.Routing.EnableFailover = false;
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var failed = new HashSet<string> { "gpt-4o" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("disabled", result.Reason);
    }

    [Fact]
    public void Apply_CoolingDownModel_RemovedFromCandidates()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        // 让 gpt-4o 进入冷却
        var tracker = new ModelHealthTracker();
        tracker.RecordFailure("gpt-4o", threshold: 1, cooldownSeconds: 60);

        var policy = NewPolicy(tracker);
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        // 无单请求失败，仅跨请求冷却应排除 gpt-4o
        var result = Apply(policy, options, candidates);

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
        Assert.Contains("cooling", result.Reason);
    }

    [Fact]
    public void Apply_HalfOpenModel_KeptInCandidatesForProbing()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        // 让 gpt-4o 熔断后冷却到期，进入半开
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);
        tracker.RecordFailure("gpt-4o", threshold: 1, cooldownSeconds: 60);
        Assert.Equal(CircuitState.Open, tracker.GetState("gpt-4o"));

        var policy = NewPolicy(tracker);
        var candidates = options.Models.Where(m => m.Enabled).ToList();

        // 冷却中：gpt-4o 被排除
        var cooling = Apply(policy, options, candidates);
        Assert.Single(cooling.Candidates);
        Assert.Equal("deepseek-chat", cooling.Candidates[0].Name);

        // 推进时钟越过冷却 → 半开：gpt-4o 应回到候选（供探测），并在 reason 中标注
        now = now.AddSeconds(61);
        var halfOpen = Apply(policy, options, candidates);
        Assert.Equal(2, halfOpen.Candidates.Count);
        Assert.Contains("half-open probing", halfOpen.Reason);
        Assert.Contains("gpt-4o", string.Join(",", halfOpen.Candidates.Select(c => c.Name)));
    }

    [Fact]
    public void Apply_CheapFailed_FallsBackToMediumBeforeStrong()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = NewPolicy();
        var candidates = options.Models.Where(m => m.Enabled && m.Tier == ModelTier.Cheap).ToList();
        var failed = new HashSet<string> { "cheap" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o-mini", result.Candidates[0].Name);
        Assert.Equal(ModelTier.Medium, result.Candidates[0].Tier);
    }

    [Fact]
    public void Apply_MediumFailed_FallsBackToStrongBeforeCheap()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = NewPolicy();
        var candidates = options.Models.Where(m => m.Enabled && m.Tier == ModelTier.Medium).ToList();
        var failed = new HashSet<string> { "gpt-4o-mini" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
        Assert.Equal(ModelTier.Strong, result.Candidates[0].Tier);
    }

    [Fact]
    public void Apply_StrongFailed_FallsBackToMediumBeforeCheap()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = NewPolicy();
        var candidates = options.Models.Where(m => m.Enabled && m.Tier == ModelTier.Strong).ToList();
        var failed = new HashSet<string> { "gpt-4o" };
        var result = Apply(policy, options, candidates, failed);

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o-mini", result.Candidates[0].Name);
        Assert.Equal(ModelTier.Medium, result.Candidates[0].Tier);
    }
}
