using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 延迟感知策略的内存 cache stub，可控注入统计。
/// </summary>
internal sealed class StubLatencyStatsProvider : ILatencyStatsProvider
{
    private readonly IReadOnlyDictionary<string, ModelLatencyStats> _stats;

    public StubLatencyStatsProvider(params (string Model, double AvgMs, int Samples)[] entries)
    {
        _stats = entries.ToDictionary(
            e => e.Model,
            e => new ModelLatencyStats(e.AvgMs, e.Samples),
            StringComparer.Ordinal);
    }

    public ModelLatencyStats? GetStats(string modelName) =>
        _stats.TryGetValue(modelName, out var s) ? s : null;

    public void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats) =>
        throw new NotSupportedException();
}

public class LatencyAwarePolicyTests
{
    private static (RouterContext Context, RouterDecision Initial) Setup(
        RouterOptions options,
        IEnumerable<ModelEndpointOptions> initialCandidates)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "hi")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };
        var initial = new RouterDecision
        {
            Candidates = initialCandidates.ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        return (context, initial);
    }

    [Fact]
    public void Apply_Disabled_PassesThrough()
    {
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = false;
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(), new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates, result.Candidates);
        Assert.Contains("latency-aware: disabled", result.Reason);
    }

    [Fact]
    public void Apply_ColdStart_NoStats_KeepsOriginalOrder()
    {
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(), new ThompsonStateStore()); // 无统计

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates, result.Candidates);
        Assert.Contains("latency-aware: no change", result.Reason);
    }

    [Fact]
    public void Apply_InsufficientSamples_KeepsOriginalOrder()
    {
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 10;
        // 两个模型样本都 < 10，不参与排序。
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(
            ("a", 500.0, 3), ("b", 100.0, 2)), new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates, result.Candidates);
        Assert.Contains("latency-aware: no change", result.Reason);
    }

    [Fact]
    public void Apply_SufficientStats_ReordersByLatencyAscending()
    {
        var options = TestHelpers.BuildOptions(
            ("slow", ModelTier.Medium, 8000, 1m),
            ("fast", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 5;
        // slow 平均 1000ms，fast 平均 100ms。fast 应排前。
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(
            ("slow", 1000.0, 50), ("fast", 100.0, 50)), new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal("fast", result.Candidates[0].Name);
        Assert.Equal("slow", result.Candidates[1].Name);
        Assert.Contains("latency-aware: reordered", result.Reason);
    }

    [Fact]
    public void Apply_MixedSamples_StatisticallySufficientFirst_InsufficientTail()
    {
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 5;
        // a/c 有充足统计，b 样本不足 → b 应在尾部。
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(
            ("a", 200.0, 20), ("c", 100.0, 20)), new ThompsonStateStore()); // b 无统计

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // c 最快在前，a 次之，b（无统计）尾部。
        Assert.Equal("c", result.Candidates[0].Name);
        Assert.Equal("a", result.Candidates[1].Name);
        Assert.Equal("b", result.Candidates[2].Name);
    }

    [Fact]
    public void Apply_CrossTierOrder_Preserved()
    {
        // Strong 段 + Cheap 段混合，延迟重排只在 tier 内，跨 tier 顺序不变。
        var options = TestHelpers.BuildOptions(
            ("s-slow", ModelTier.Strong, 8000, 5m),
            ("s-fast", ModelTier.Strong, 8000, 5m),
            ("c-slow", ModelTier.Cheap, 8000, 0.01m),
            ("c-fast", ModelTier.Cheap, 8000, 0.01m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 5;
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(
            ("s-slow", 1000.0, 20), ("s-fast", 100.0, 20),
            ("c-slow", 800.0, 20), ("c-fast", 80.0, 20)), new ThompsonStateStore());

        // 初始候选：Strong 段在前，Cheap 段在后。
        var initial = new RouterDecision
        {
            Candidates = options.Models.ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "hi")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };

        var result = policy.Apply(context, initial);

        // Strong 段：s-fast, s-slow；Cheap 段：c-fast, c-slow。跨 tier 不变。
        Assert.Equal("s-fast", result.Candidates[0].Name);
        Assert.Equal("s-slow", result.Candidates[1].Name);
        Assert.Equal("c-fast", result.Candidates[2].Name);
        Assert.Equal("c-slow", result.Candidates[3].Name);
    }

    [Fact]
    public void Apply_SingleCandidate_PassesThrough()
    {
        var options = TestHelpers.BuildOptions(("only", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(("only", 100.0, 50)), new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates, result.Candidates);
        Assert.Contains("latency-aware: <2 candidates", result.Reason);
    }
}
