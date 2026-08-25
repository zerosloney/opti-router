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

    [Fact]
    public void Apply_EpsilonExploration_PromotesTailModel_WhenSampled()
    {
        // ε 探索保底：sampleUniform 固定返回 0.1 < epsilon=0.5 时，应把段内非首位模型提到段首。
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;

        // 延迟统计让 a 排第一（顺序 a,b,c）。
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            new ThompsonStateStore(),
            sampleBeta: null,
            banditStore: null,
            // 第一次调用返回 0.1（触发探索），第二次调用返回 0.9（决定是否跳过时不触发，但这里只调用一次 MaybePromoteTailForExploration）。
            sampleUniform: () => 0.1);

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 段首应为 b 或 c（index=1 或 2），不再是 a。
        Assert.NotEqual("a", result.Candidates[0].Name);
        Assert.Contains(result.Candidates[0].Name, new[] { "b", "c" });
        Assert.Contains("ε-explore promoted", result.Reason);
    }

    [Fact]
    public void Apply_EpsilonExploration_Disabled_NoPromotion()
    {
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.0;

        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50)),
            new ThompsonStateStore(),
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.0);

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal("a", result.Candidates[0].Name);
        Assert.Equal("b", result.Candidates[1].Name);
        Assert.DoesNotContain("ε-explore promoted", result.Reason);
    }

    [Fact]
    public void Apply_EpsilonExploration_SetsPromotedModelInDecision()
    {
        // ε 探索保底：验证被提升的模型名被写入 RouterDecision.EpsilonPromotedModel
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;

        // 模拟 sampleUniform 返回 0.4（触发探索），0.4 * 2 = 0.8 → index=1，提升 b
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            new ThompsonStateStore(),
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.4); // 0.4 * (3-1) = 0.8 → int index=1 → b

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 验证 b 被提升到段首
        Assert.Equal("b", result.Candidates[0].Name);
        Assert.Contains("ε-explore promoted", result.Reason);
        
        // 验证 RouterDecision.EpsilonPromotedModel 被设置为被提升的模型名
        Assert.Equal("b", result.EpsilonPromotedModel);
    }

    [Fact]
    public void Apply_NoEpsilonExploration_PromotedModelRemainsNull()
    {
        // 无探索时，EpsilonPromotedModel 应保持 null
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.0;

        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50)),
            new ThompsonStateStore(),
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.9); // > epsilon，不触发探索

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 验证无提升发生
        Assert.DoesNotContain("ε-explore promoted", result.Reason);

        // 验证 EpsilonPromotedModel 为 null
        Assert.Null(result.EpsilonPromotedModel);
    }

    [Fact]
    public void Apply_StarvedExploration_PromotesFromStarvedSet()
    {
        // 定向探索：阈值>0 且段内有饥饿模型（N=0）时，提升的模型应来自饥饿集合
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;
        options.Routing.ExplorationStarvedN = 10; // 阈值 10

        // 延迟统计让 a 排第一（顺序 a,b,c）
        var tsStore = new ThompsonStateStore();
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            tsStore,
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.1); // 触发探索

        // 预填样本：a=20（充足），b=0（饥饿），c=0（饥饿）
        for (int i = 0; i < 20; i++) tsStore.RecordOutcome("a", 0.8, discountFactor: 1.0);
        // b、c 保持 N=0

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 段首应为 b 或 c（饥饿模型），不再是 a
        Assert.NotEqual("a", result.Candidates[0].Name);
        Assert.Contains(result.Candidates[0].Name, new[] { "b", "c" });
        Assert.Contains("ε-explore promoted", result.Reason);
    }

    [Fact]
    public void Apply_StarvedExploration_AllSatisfied_FallbackToUniform()
    {
        // 定向探索：阈值>0 但所有模型样本充足时，回退均匀行为（与旧行为一致）
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;
        options.Routing.ExplorationStarvedN = 5; // 阈值 5

        var tsStore = new ThompsonStateStore();
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            tsStore,
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.4); // 触发探索，0.4 * 2 = 0.8 → index=1 → b

        // 预填样本：a=20，b=20，c=20（全部充足）
        for (int i = 0; i < 20; i++)
        {
            tsStore.RecordOutcome("a", 0.8, discountFactor: 1.0);
            tsStore.RecordOutcome("b", 0.7, discountFactor: 1.0);
            tsStore.RecordOutcome("c", 0.6, discountFactor: 1.0);
        }

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 回退均匀行为：应提升 index=1 的 b（0.4 * (3-1) = 0.8）
        Assert.Equal("b", result.Candidates[0].Name);
        Assert.Contains("ε-explore promoted", result.Reason);
    }

    [Fact]
    public void Apply_StarvedExploration_ThresholdZero_BehavesLikeLegacy()
    {
        // 定向探索：阈值=0 时行为与改前一致（回归测试）
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;
        options.Routing.ExplorationStarvedN = 0; // 阈值 0 = 关闭定向

        var tsStore = new ThompsonStateStore();
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            tsStore,
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () => 0.4); // 触发探索，0.4 * 2 = 0.8 → index=1 → b

        // 预填样本：a=20，b=0，c=0（即使有饥饿模型，阈值=0 也不定向）
        for (int i = 0; i < 20; i++) tsStore.RecordOutcome("a", 0.8, discountFactor: 1.0);

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 均匀行为：应提升 index=1 的 b
        Assert.Equal("b", result.Candidates[0].Name);
        Assert.Contains("ε-explore promoted", result.Reason);
    }

    [Fact]
    public void Apply_StarvedExploration_SetsPromotedModelInDecision()
    {
        // 验证定向探索时，EpsilonPromotedModel 字段同样正确写入决策
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m),
            ("c", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.LatencyMinSamples = 1;
        options.Routing.ExplorationEpsilon = 0.5;
        options.Routing.ExplorationStarvedN = 10;

        var tsStore = new ThompsonStateStore();
        // 模拟 sampleUniform 返回 0.5（触发探索），0.5 * 1 = 0.5 → 饥饿集合 [b, c] 的 index=0 → b
        double uniformCallCount = 0;
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(("a", 100.0, 50), ("b", 200.0, 50), ("c", 300.0, 50)),
            tsStore,
            sampleBeta: null,
            banditStore: null,
            sampleUniform: () =>
            {
                uniformCallCount++;
                // 第一次调用判断是否触发探索（0.5 < 0.5 触发）
                // 第二次调用从饥饿集合选（0.5 * 2 = 1.0 → index=1，取整为 1 → 饥饿集合 index=1 → c）
                // 但实际上我们控制逻辑：第一次 0.1，第二次 0.9（选择第二个饥饿模型）
                return uniformCallCount == 1 ? 0.1 : 0.9;
            });

        // 预填样本：a=20（充足），b=0（饥饿），c=0（饥饿）
        for (int i = 0; i < 20; i++) tsStore.RecordOutcome("a", 0.8, discountFactor: 1.0);

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 验证 c 被提升到段首（第二个饥饿模型）
        Assert.Equal("c", result.Candidates[0].Name);
        Assert.Contains("ε-explore promoted", result.Reason);

        // 验证 RouterDecision.EpsilonPromotedModel 被设置为被提升的模型名
        Assert.Equal("c", result.EpsilonPromotedModel);
    }

    // ===== 强档延迟降级 pre-pass（Commit 3） =====

    [Fact]
    public void DegradeSlowStrong_DisabledByDefault_PassesThrough()
    {
        // LatencyDegradeStrongP95Ms=0 (默认关闭) → 即使有慢强档也不动
        var options = TestHelpers.BuildOptions(
            ("strong-slow", ModelTier.Strong, 128000, 5m),
            ("strong-fast", ModelTier.Strong, 128000, 5m),
            ("medium-a", ModelTier.Medium, 64000, 0.15m));
        var stats = StubLatencyStatsProvider.WithP95(
            ("strong-slow", 100_000, 200_000, 10),
            ("strong-fast", 5_000, 8_000, 10));
        var policy = new LatencyAwarePolicy(stats, new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates.Select(m => m.Name), result.Candidates.Select(m => m.Name));
    }

    [Fact]
    public void DegradeSlowStrong_AboveThreshold_MovesSlowToTail()
    {
        // strong-slow p95=100s > 30s 阈值 → 移到 candidates 末尾
        // strong-fast p95=8s < 30s 阈值 → 保留原位
        var options = TestHelpers.BuildOptions(
            ("strong-slow", ModelTier.Strong, 128000, 5m),
            ("strong-fast", ModelTier.Strong, 128000, 5m),
            ("medium-a", ModelTier.Medium, 64000, 0.15m));
        options.Routing.LatencyDegradeStrongP95Ms = 30_000;
        var stats = StubLatencyStatsProvider.WithP95(
            ("strong-slow", 50_000, 100_000, 10),
            ("strong-fast", 5_000, 8_000, 10));
        var policy = new LatencyAwarePolicy(stats, new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // 末尾应是 strong-slow
        Assert.Equal("strong-slow", result.Candidates[^1].Name);
        Assert.Contains("degraded 1 slow-strong", result.Reason);
    }

    [Fact]
    public void DegradeSlowStrong_InsufficientSamples_NotDegraded()
    {
        // 强档 p95 超阈值但样本数 < minSamples → 不动（冷启动保护）
        var options = TestHelpers.BuildOptions(
            ("strong-slow", ModelTier.Strong, 128000, 5m),
            ("medium-a", ModelTier.Medium, 64000, 0.15m));
        options.Routing.LatencyDegradeStrongP95Ms = 30_000;
        options.Routing.LatencyMinSamples = 5;
        var stats = StubLatencyStatsProvider.WithP95(
            ("strong-slow", 50_000, 100_000, 3));  // samples=3 < 5
        var policy = new LatencyAwarePolicy(stats, new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates.Select(m => m.Name), result.Candidates.Select(m => m.Name));
    }

    [Fact]
    public void DegradeSlowStrong_OnlyStrongAffected_MediumUntouched()
    {
        // 即使 medium p95 也很高，也只动 Strong；medium 保留原位
        var options = TestHelpers.BuildOptions(
            ("strong-slow", ModelTier.Strong, 128000, 5m),
            ("medium-slow", ModelTier.Medium, 64000, 0.15m),
            ("medium-fast", ModelTier.Medium, 32000, 0.1m));
        options.Routing.LatencyDegradeStrongP95Ms = 30_000;
        var stats = StubLatencyStatsProvider.WithP95(
            ("strong-slow", 50_000, 100_000, 10),
            ("medium-slow", 30_000, 80_000, 10),  // 也很慢但不动
            ("medium-fast", 5_000, 8_000, 10));
        var policy = new LatencyAwarePolicy(stats, new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        // strong-slow 移到末尾；medium-slow 保留原位
        Assert.Equal("strong-slow", result.Candidates[^1].Name);
        Assert.DoesNotContain("strong-slow", result.Candidates.Take(2).Select(m => m.Name));
    }

    [Fact]
    public void DegradeSlowStrong_AllThreeDisabled_ButDegradeEnabled_StillWorks()
    {
        // LatencyAware/Thompson/Bandit 全关，但 LatencyDegradeStrongP95Ms 开启 → 仍生效
        // （pre-pass 独立于段内 reorder 三个开关）
        var options = TestHelpers.BuildOptions(
            ("strong-slow", ModelTier.Strong, 128000, 5m),
            ("strong-fast", ModelTier.Strong, 128000, 5m),
            ("medium-a", ModelTier.Medium, 64000, 0.15m));
        options.Routing.EnableLatencyAware = false;
        options.Routing.EnableThompsonSampling = false;
        options.Routing.EnableContextualBandit = false;
        options.Routing.LatencyDegradeStrongP95Ms = 30_000;
        var stats = StubLatencyStatsProvider.WithP95(
            ("strong-slow", 50_000, 100_000, 10),
            ("strong-fast", 5_000, 8_000, 10));
        var policy = new LatencyAwarePolicy(stats, new ThompsonStateStore());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal("strong-slow", result.Candidates[^1].Name);
        Assert.Contains("degraded 1 slow-strong", result.Reason);
    }
}
