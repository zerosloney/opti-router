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

    [Fact]
    public void Apply_LongInput_ForceMediumOff_DoesNotFilterStrong()
    {
        // force-medium = false（默认）→ 既有行为不变：长 prompt 仅按上下文过滤，不动 tier
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));
        options.Routing.LongInputForceMedium = false;

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 40000, candidates.ToArray());

        Assert.Equal(2, result.Candidates.Count); // Strong + Medium 都保留
        Assert.DoesNotContain("dropped-strong", result.Reason);
    }

    [Fact]
    public void Apply_LongInput_ForceMediumOn_DropsStrongTier()
    {
        // force-medium = true + 长 prompt → 排除 Strong，只留 Medium
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("claude-haiku", ModelTier.Cheap, 64000, 0.05m));
        options.Routing.LongInputForceMedium = true;

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 40000, candidates.ToArray());

        Assert.Equal(2, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, m => m.Tier == ModelTier.Strong);
        Assert.Contains("gpt-4o-mini", result.Candidates.Select(m => m.Name));
        Assert.Contains("dropped-strong=1", result.Reason);
    }

    [Fact]
    public void Apply_LongInput_ForceMediumOn_OnlyStrongAvailable_KeepsStrongAsLastResort()
    {
        // force-medium = true 但只剩 Strong 能装 → 保留 Strong（避免无候选可路由）
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("small-model", ModelTier.Cheap, 8000, 0.005m));
        options.Routing.LongInputForceMedium = true;

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 40000, candidates.ToArray());

        // gpt-4o 是唯一能装下 40000*1.2=48000 的；保留它，但 reason 仍标 dropped-strong=0
        // （因为已先被上下文过滤排除，第二次 tier 过滤没有再丢任何东西）
        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
        // 不出现 dropped-strong 标记——因为第一次按上下文过滤就已经排掉了 Medium/Cheap
        Assert.DoesNotContain("dropped-strong", result.Reason);
    }

    [Fact]
    public void Apply_ShortInput_ForceMediumOn_RecordsArmedButNotTriggered()
    {
        // force-medium = true + 短 prompt → 不应改候选，但 reason 标记"开关已上膛"
        // 便于排查"配置是否真的生效"
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));
        options.Routing.LongInputForceMedium = true;

        var policy = new LongInputPolicy();
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, 1000, candidates.ToArray());

        Assert.Equal(2, result.Candidates.Count); // 短 prompt 不动
        Assert.Contains("within-threshold", result.Reason);
        Assert.Contains("force-medium-armed-but-not-triggered", result.Reason);
    }
}
