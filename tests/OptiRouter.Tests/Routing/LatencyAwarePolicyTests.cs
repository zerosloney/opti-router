using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 延迟感知策略的内存 cache stub，可控注入统计。
/// </summary>
internal sealed class StubLatencyStatsProvider : ILatencyStatsProvider
{
    private IReadOnlyDictionary<string, ModelLatencyStats> _stats;

    public StubLatencyStatsProvider(params (string Model, double AvgMs, int Samples)[] entries)
    {
        // p95 默认取 avg（既有无 p95 语义的测试行为不变）。
        _stats = entries.ToDictionary(
            e => e.Model,
            e => new ModelLatencyStats(e.AvgMs, e.AvgMs, e.Samples),
            StringComparer.Ordinal);
    }

    /// <summary>带显式 p95 的便捷构造（避免与三元组 params 重载二义）。</summary>
    public static StubLatencyStatsProvider WithP95(params (string Model, double AvgMs, double P95Ms, int Samples)[] entries)
    {
        var provider = new StubLatencyStatsProvider();
        provider._stats = entries.ToDictionary(
            e => e.Model,
            e => new ModelLatencyStats(e.AvgMs, e.P95Ms, e.Samples),
            StringComparer.Ordinal);
        return provider;
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

    [Fact]
    public void Apply_TailLatency_P95BetterWins_WhenAvgClose()
    {
        // 根治型关键场景：两个模型平均延迟接近，但 p95 差异大 → tail 优者（p95 更低）应排前。
        // 旧评分只看 avg，无法区分；新评分 = 1/(avg + 0.5×p95 + 50)。
        var options = TestHelpers.BuildOptions(
            ("stable", ModelTier.Medium, 8000, 1m),
            ("spiky", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 5;
        // 两者 avg 相同 200ms；stable 的 p95=210（tail 稳），spiky 的 p95=900（tail 抖）。
        var policy = new LatencyAwarePolicy(StubLatencyStatsProvider.WithP95(
            ("stable", 200.0, 210.0, 50), ("spiky", 200.0, 900.0, 50)), new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // stable: 1/(200+105+50)=1/355；spiky: 1/(200+450+50)=1/700 → stable 分高在前。
        Assert.Equal("stable", result.Candidates[0].Name);
        Assert.Equal("spiky", result.Candidates[1].Name);
    }
}
