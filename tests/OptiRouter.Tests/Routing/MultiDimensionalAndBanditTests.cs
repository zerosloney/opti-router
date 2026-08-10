using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public sealed class MultiDimensionalAndBanditTests
{
    private static (RouterContext Context, RouterDecision Initial) Setup(
        RouterOptions options,
        IEnumerable<ModelEndpointOptions> initialCandidates,
        string query)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", query)),
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
    public void MultiDimensionalRouting_CalculatesMatchScoreAndSortsCorrectly()
    {
        var options = new RouterOptions();
        options.Routing.EnableRuleClassifier = true;
        options.Routing.EnableMultiDimensionalRouting = true;

        var m1 = new ModelEndpointOptions
        {
            Name = "custom-coder",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.5m
        };
        m1.Capabilities["coding"] = 0.95;
        m1.Capabilities["reasoning"] = 0.80;

        var m2 = new ModelEndpointOptions
        {
            Name = "fallback-medium",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.4m
        }; // 没有 Capabilities，回退到 Tier.Medium 基准 (0.6)

        var m3 = new ModelEndpointOptions
        {
            Name = "cheap-coder",
            Tier = ModelTier.Cheap,
            Enabled = true,
            InputPricePerMillion = 0.05m
        };
        m3.Capabilities["coding"] = 0.90;
        m3.Capabilities["reasoning"] = 0.50;

        options.Models.Add(m1);
        options.Models.Add(m2);
        options.Models.Add(m3);

        var policy = new RuleClassifierPolicy();

        // 1. 包含代码块/定义，触发 code-detected
        var (ctx, initial) = Setup(options, options.Models, "写个 Python 排序 ```python\ndef quicksort(): pass\n```");
        var result = policy.Apply(ctx, initial);

        Assert.Contains("multi-dimensional active", result.Reason);
        Assert.Contains("coding", result.Reason);

        // 期待排序：
        // "custom-coder" (coding = 0.95) 第一
        // "cheap-coder" (coding = 0.90) 第二
        // "fallback-medium" (fallback coding = 0.6) 第三
        Assert.Equal("custom-coder", result.Candidates[0].Name);
        Assert.Equal("cheap-coder", result.Candidates[1].Name);
        Assert.Equal("fallback-medium", result.Candidates[2].Name);
    }

    [Fact]
    public void MultiDimensionalRouting_CloseScores_CheaperWinsByTolerance()
    {
        // Spec "Base" case 的精确复现：能力分差落在容差内 → 价格择廉。
        // 防止未来有人误改 CapabilityScoreTolerance 或排序比较器导致成本优化失效。
        var options = new RouterOptions();
        options.Routing.EnableRuleClassifier = true;
        options.Routing.EnableMultiDimensionalRouting = true;

        var expensive = new ModelEndpointOptions
        {
            Name = "stronger-language",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.5m
        };
        expensive.Capabilities["language"] = 0.95;

        var cheaper = new ModelEndpointOptions
        {
            Name = "cheaper-language",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.05m
        };
        cheaper.Capabilities["language"] = 0.93;

        options.Models.Add(expensive);
        options.Models.Add(cheaper);

        var policy = new RuleClassifierPolicy();

        // 简单 QA → language=1.0 weights；Scores: expensive=0.95, cheaper=0.93（diff=0.02 <= 0.15 tolerance）
        var (ctx, initial) = Setup(options, options.Models, "你好");
        var result = policy.Apply(ctx, initial);

        // 能力相近（diff <= 容差）：便宜模型应胜出
        Assert.Equal("cheaper-language", result.Candidates[0].Name);
        Assert.Equal("stronger-language", result.Candidates[1].Name);
    }

    [Fact]
    public void MultiDimensionalRouting_LargeScoreGap_CapabilityWinsOverPrice()
    {
        // 容差边界另一侧：分差超过容差 → 能力主导排序，价格不参与。
        // 与 CloseScores 一起锁定 CapabilityScoreTolerance 的边界语义。
        var options = new RouterOptions();
        options.Routing.EnableRuleClassifier = true;
        options.Routing.EnableMultiDimensionalRouting = true;

        var strong = new ModelEndpointOptions
        {
            Name = "much-better",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.5m
        };
        strong.Capabilities["language"] = 0.95;

        var weak = new ModelEndpointOptions
        {
            Name = "much-weaker-cheap",
            Tier = ModelTier.Medium,
            Enabled = true,
            InputPricePerMillion = 0.01m
        };
        weak.Capabilities["language"] = 0.50;

        options.Models.Add(strong);
        options.Models.Add(weak);

        var policy = new RuleClassifierPolicy();

        // language weights → Scores: strong=0.95, weak=0.50（diff=0.45 > 0.15 tolerance）
        var (ctx, initial) = Setup(options, options.Models, "你好");
        var result = policy.Apply(ctx, initial);

        // 能力显著领先：即使更贵也应排前
        Assert.Equal("much-better", result.Candidates[0].Name);
        Assert.Equal("much-weaker-cheap", result.Candidates[1].Name);
    }

    [Fact]
    public void MultiDimensionalRouting_LanguageTask_CheapWinsOverStrong_ByPrice()
    {
        // 根治型关键场景：语言是廉价维度（档距近扁平），未显式配置能力时，
        // 纯语言任务（simple-qa）下 Strong 与 Cheap 的语言分数应落入同桶 → 价格择廉。
        // 旧实现对所有维度回退同一 tier 值（Strong 0.9 vs Cheap 0.3），此处 Cheap 永远赢不了。
        var options = new RouterOptions();
        options.Routing.EnableRuleClassifier = true;
        options.Routing.EnableMultiDimensionalRouting = true;

        var strong = new ModelEndpointOptions
        {
            Name = "strong-language",
            Tier = ModelTier.Strong,
            Enabled = true,
            InputPricePerMillion = 5m
        }; // 无 Capabilities → 语言回退 0.80

        var cheap = new ModelEndpointOptions
        {
            Name = "cheap-language",
            Tier = ModelTier.Cheap,
            Enabled = true,
            InputPricePerMillion = 0.01m
        }; // 无 Capabilities → 语言回退 0.76

        options.Models.Add(strong);
        options.Models.Add(cheap);

        var policy = new RuleClassifierPolicy();

        // simple-qa → language=1.0, reasoning=0.1
        // strong: 1.0×0.80 + 0.1×0.90 = 0.89 → 桶 5
        // cheap:  1.0×0.76 + 0.1×0.20 = 0.78 → 桶 5
        // 同桶 → 价格升序 → cheap 胜
        var (ctx, initial) = Setup(options, options.Models, "你好");
        var result = policy.Apply(ctx, initial);

        Assert.Equal("cheap-language", result.Candidates[0].Name);
        Assert.Equal("strong-language", result.Candidates[1].Name);
    }

    [Fact]
    public void MultiDimensionalRouting_ReasoningTask_StrongWinsOverCheap_ByCapability()
    {
        // 根治型关键场景反面：推理是昂贵维度（档距陡），未显式配置能力时，
        // 数学/推理任务下 Strong 因推理分数分差胜出。
        var options = new RouterOptions();
        options.Routing.EnableRuleClassifier = true;
        options.Routing.EnableMultiDimensionalRouting = true;

        var strong = new ModelEndpointOptions
        {
            Name = "strong-reasoner",
            Tier = ModelTier.Strong,
            Enabled = true,
            InputPricePerMillion = 5m
        }; // 推理回退 0.90

        var cheap = new ModelEndpointOptions
        {
            Name = "cheap-reasoner",
            Tier = ModelTier.Cheap,
            Enabled = true,
            InputPricePerMillion = 0.01m
        }; // 推理回退 0.20

        options.Models.Add(strong);
        options.Models.Add(cheap);

        var policy = new RuleClassifierPolicy();

        // math-detected → reasoning=1.0, coding=0.5, language=0.3
        // strong: 1.0×0.90 + 0.5×0.90 + 0.3×0.80 = 1.59 → 桶 10
        // cheap:  1.0×0.20 + 0.5×0.30 + 0.3×0.76 = 0.578 → 桶 3
        // 分差大 → strong 胜
        var (ctx, initial) = Setup(options, options.Models, "求解这个微分方程: dy/dx = 2x");
        var result = policy.Apply(ctx, initial);

        Assert.Equal("strong-reasoner", result.Candidates[0].Name);
        Assert.Equal("cheap-reasoner", result.Candidates[1].Name);
    }

    [Fact]
    public void GetEffectiveCapability_ExplicitCapabilities_TakesPriority()
    {
        // 显式配置的 Capabilities 始终优先于维度回退表。
        var m = new ModelEndpointOptions
        {
            Name = "explicit",
            Tier = ModelTier.Cheap,
            Enabled = true
        };
        m.Capabilities["language"] = 0.99;
        m.Capabilities["coding"] = 0.95;

        Assert.Equal(0.99, m.GetEffectiveCapability("language"));
        Assert.Equal(0.95, m.GetEffectiveCapability("coding"));
        // 未配置的推理维度走 Cheap 回退 0.20
        Assert.Equal(0.20, m.GetEffectiveCapability("reasoning"));
    }

    [Fact]
    public void GetEffectiveCapability_UnknownDimension_ReturnsNeutral()
    {
        // 未知维度（非 coding/reasoning/language）保守回退 0.5，不偏向任何档。
        var strong = new ModelEndpointOptions { Name = "s", Tier = ModelTier.Strong, Enabled = true };
        var cheap = new ModelEndpointOptions { Name = "c", Tier = ModelTier.Cheap, Enabled = true };

        Assert.Equal(0.5, strong.GetEffectiveCapability("vision-quality"));
        Assert.Equal(0.5, cheap.GetEffectiveCapability("vision-quality"));
    }

    [Fact]
    public void GetEffectiveCapability_DimensionFallback_ByTier()
    {
        // 维度回退表：语言近扁平（0.80/0.78/0.76），推理陡（0.90/0.50/0.20），代码陡（0.90/0.60/0.30）。
        var strong = new ModelEndpointOptions { Name = "s", Tier = ModelTier.Strong, Enabled = true };
        var medium = new ModelEndpointOptions { Name = "m", Tier = ModelTier.Medium, Enabled = true };
        var cheap = new ModelEndpointOptions { Name = "c", Tier = ModelTier.Cheap, Enabled = true };

        Assert.Equal(0.80, strong.GetEffectiveCapability("language"));
        Assert.Equal(0.78, medium.GetEffectiveCapability("language"));
        Assert.Equal(0.76, cheap.GetEffectiveCapability("language"));

        Assert.Equal(0.90, strong.GetEffectiveCapability("reasoning"));
        Assert.Equal(0.50, medium.GetEffectiveCapability("reasoning"));
        Assert.Equal(0.20, cheap.GetEffectiveCapability("reasoning"));

        Assert.Equal(0.90, strong.GetEffectiveCapability("coding"));
        Assert.Equal(0.60, medium.GetEffectiveCapability("coding"));
        Assert.Equal(0.30, cheap.GetEffectiveCapability("coding"));
    }

    [Fact]
    public void ThompsonSampler_SamplesValidValues()
    {
        for (int i = 0; i < 50; i++)
        {
            double s1 = ThompsonSampler.SampleBeta(1.0, 1.0);
            Assert.True(s1 > 0.0 && s1 < 1.0);

            double s2 = ThompsonSampler.SampleBeta(10.0, 1.0);
            Assert.True(s2 > 0.0 && s2 < 1.0);

            double s3 = ThompsonSampler.SampleBeta(1.0, 10.0);
            Assert.True(s3 > 0.0 && s3 < 1.0);
        }
    }

    [Fact]
    public void ThompsonSampler_BetaShape_MeanReflectsAlphaBetaRatio()
    {
        // 分布断言：Beta(50,1) 偏向 1（高成功），Beta(1,50) 偏向 0（低成功）。
        // 仅断言 (0,1) 范围（ThompsonSampler_SamplesValidValues）无法捕获退化成均匀分布的 bug。
        // seeded RNG 保证确定性；均值容差用大数定律 3000 样本收敛。
        var rngHigh = new Random(1);
        var rngLow = new Random(2);
        double sumHigh = 0, sumLow = 0;
        const int N = 3000;
        for (int i = 0; i < N; i++)
        {
            sumHigh += ThompsonSampler.SampleBeta(50.0, 1.0, rngHigh);
            sumLow += ThompsonSampler.SampleBeta(1.0, 50.0, rngLow);
        }
        double meanHigh = sumHigh / N;
        double meanLow = sumLow / N;

        // Beta(50,1) 均值 = 50/51 ≈ 0.98；Beta(1,50) 均值 = 1/51 ≈ 0.02。给宽松容差防噪声。
        Assert.True(meanHigh > 0.90, $"Beta(50,1) 均值应 >0.90，实际 {meanHigh:F3}");
        Assert.True(meanLow < 0.10, $"Beta(1,50) 均值应 <0.10，实际 {meanLow:F3}");
    }

    [Fact]
    public void ThompsonStateStore_UpdatesParametersWithDiscount()
    {
        var store = new ThompsonStateStore();
        string modelName = "test-model";

        var stats = store.GetOrAdd(modelName);
        Assert.Equal(1.0, stats.Alpha);
        Assert.Equal(1.0, stats.Beta);

        // 记录一次好响应，折扣因子 0.9
        store.RecordOutcome(modelName, isGood: true, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 1.0, stats.Alpha); // 1.9
        Assert.Equal(1.0 * 0.9 + 0.0, stats.Beta);  // 0.9

        // 记录一次差响应，折扣因子 0.9
        store.RecordOutcome(modelName, isGood: false, discountFactor: 0.9);
        Assert.Equal(1.9 * 0.9 + 0.0, stats.Alpha); // 1.71
        Assert.Equal(0.9 * 0.9 + 1.0, stats.Beta);  // 1.81
    }

    [Theory]
    [InlineData(2.0)]   // 超上限：Math.Clamp 截到 1.0，等效无衰减全量保留
    [InlineData(-5.0)]  // 超下限：Math.Clamp 截到 0.1，强衰减
    [InlineData(0.0)]
    public void ThompsonStateStore_RecordOutcome_ClampsDiscountFactor(double factor)
    {
        // 验证 Math.Clamp(discountFactor, 0.1, 1.0) 防护：非法因子不应抛异常或破坏状态。
        var store = new ThompsonStateStore();
        var stats = store.GetOrAdd("clamp-model");
        double alphaBefore = stats.Alpha;

        store.RecordOutcome("clamp-model", isGood: true, discountFactor: factor);

        double clamped = Math.Clamp(factor, 0.1, 1.0);
        Assert.Equal(alphaBefore * clamped + 1.0, stats.Alpha);
    }

    [Fact]
    public void ThompsonStateStore_RecordOutcome_ContinuousReward_MapsTriState()
    {
        // 连续奖励：reward=1.0（快成功）→ 全 Alpha；0.0（硬失败）→ 全 Beta；0.3（慢成功）→ 部分 Alpha + 部分 Beta。
        var store = new ThompsonStateStore();

        var fast = store.GetOrAdd("fast");
        store.RecordOutcome("fast", 1.0, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 1.0, fast.Alpha);
        Assert.Equal(1.0 * 0.9 + 0.0, fast.Beta);

        var slow = store.GetOrAdd("slow");
        store.RecordOutcome("slow", 0.3, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 0.3, slow.Alpha);
        Assert.Equal(1.0 * 0.9 + 0.7, slow.Beta);

        var fail = store.GetOrAdd("fail");
        store.RecordOutcome("fail", 0.0, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 0.0, fail.Alpha);
        Assert.Equal(1.0 * 0.9 + 1.0, fail.Beta);
    }

    [Fact]
    public void ThompsonStateStore_RecordOutcome_RewardClampedToRange()
    {
        // reward 越界钳制到 [0,1]：负值按 0（等效硬失败），>1 按 1（等效快成功）。
        var store = new ThompsonStateStore();
        var stats = store.GetOrAdd("clamp-reward");

        store.RecordOutcome("clamp-reward", -5.0, 0.9);
        Assert.Equal(1.0 * 0.9 + 0.0, stats.Alpha);
        Assert.Equal(1.0 * 0.9 + 1.0, stats.Beta);

        store.RecordOutcome("clamp-reward", 3.0, 0.9);
        Assert.Equal((1.0 * 0.9 + 0.0) * 0.9 + 1.0, stats.Alpha);
        Assert.Equal((1.0 * 0.9 + 1.0) * 0.9 + 0.0, stats.Beta);
    }

    [Fact]
    public void ThompsonStateStore_RecordOutcome_BoolOverload_DelegatesToReward()
    {
        // 二值兼容重载应委托到连续奖励重载：true → reward 1.0，false → reward 0.0。
        var store = new ThompsonStateStore();

        var good = store.GetOrAdd("good");
        store.RecordOutcome("good", isGood: true, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 1.0, good.Alpha);
        Assert.Equal(1.0 * 0.9 + 0.0, good.Beta);

        var bad = store.GetOrAdd("bad");
        store.RecordOutcome("bad", isGood: false, discountFactor: 0.9);
        Assert.Equal(1.0 * 0.9 + 0.0, bad.Alpha);
        Assert.Equal(1.0 * 0.9 + 1.0, bad.Beta);
    }

    [Fact]
    public void RecordThompsonRaceCancelled_GivesPartialReward_BetweenFailureAndFastSuccess()
    {
        // 竞速失败（被更快模型比下去而取消）：应获独立部分奖励（0.5），
        // 高于硬失败（0.0）、低于快成功（1.0），且与慢成功（0.3）区分。
        var store = new ThompsonStateStore();
        var opts = new RouterOptions(); // 默认 ThompsonDiscountFactor=0.95
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: new StubOptionsMonitor(opts),
            affinityCache: new MemoryCache(new MemoryCacheOptions()),
            tsStore: store,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: null!);

        recorder.RecordThompsonRaceCancelled("race-model");
        var stats = store.GetOrAdd("race-model");

        // reward=0.5：Alpha = 1.0×0.95 + 0.5 = 1.45；Beta = 1.0×0.95 + (1-0.5) = 1.45
        Assert.Equal(1.0 * 0.95 + 0.5, stats.Alpha);
        Assert.Equal(1.0 * 0.95 + 0.5, stats.Beta);
    }

    [Fact]
    public void RecordThompsonRaceCancelled_Distinct_FromHardFailure()
    {
        // 竞速失败（0.5）与真失败（0.0）必须产生不同的 Alpha/Beta 状态，才能区分「慢但未必坏」与「真故障」。
        var store = new ThompsonStateStore();
        var opts = new RouterOptions();
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: new StubOptionsMonitor(opts),
            affinityCache: new MemoryCache(new MemoryCacheOptions()),
            tsStore: store,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: null!);

        recorder.RecordThompsonRaceCancelled("cancelled");
        recorder.RecordThompsonOutcome("hard-fail", null); // 真失败 → 0.0

        var cancelled = store.GetOrAdd("cancelled");
        var hardFail = store.GetOrAdd("hard-fail");

        // cancelled: Alpha=1.45, Beta=1.45；hard-fail: Alpha=0.95, Beta=1.95
        Assert.NotEqual(hardFail.Alpha, cancelled.Alpha);
        Assert.NotEqual(hardFail.Beta, cancelled.Beta);
        // 竞速失败 Alpha 更高（更接近正反馈），真失败 Beta 更高（更多惩罚）。
        Assert.True(cancelled.Alpha > hardFail.Alpha);
        Assert.True(hardFail.Beta > cancelled.Beta);
    }

    [Fact]
    public void RecordThompsonRaceCancelled_UsesConfigurableReward()
    {
        // 竞速失败奖励应为运行时配置项（Reload 生效），而非编译期常量。
        // 设 ThompsonRaceCancelledReward=0.7，验证 store 状态反映配置值而非默认 0.5。
        var store = new ThompsonStateStore();
        var opts = new RouterOptions();
        opts.Routing.ThompsonRaceCancelledReward = 0.7;
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: new StubOptionsMonitor(opts),
            affinityCache: new MemoryCache(new MemoryCacheOptions()),
            tsStore: store,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: null!);

        recorder.RecordThompsonRaceCancelled("cfg-model");
        var stats = store.GetOrAdd("cfg-model");

        // reward=0.7（配置值，非默认 0.5）：Alpha = 1.0×0.95 + 0.7 = 1.65；Beta = 1.0×0.95 + 0.3 = 1.25
        Assert.Equal(1.0 * 0.95 + 0.7, stats.Alpha);
        Assert.Equal(1.0 * 0.95 + 0.3, stats.Beta);
    }

    [Fact]
    public void LatencyAwarePolicy_WithThompsonSampling_ReordersCorrectly()
    {
        // 先定义 m-bad 后定义 m-good，初始顺序为 [m-bad, m-good]
        var options = TestHelpers.BuildOptions(
            ("m-bad", ModelTier.Medium, 8000, 1m),
            ("m-good", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = true;
        options.Routing.EnableThompsonSampling = true;

        var store = new ThompsonStateStore();
        // 给 m-good 积累极佳的先验成功数据
        for (int i = 0; i < 100; i++)
        {
            store.RecordOutcome("m-good", isGood: true, discountFactor: 0.95);
        }
        // 给 m-bad 积累极差的失败数据
        for (int i = 0; i < 100; i++)
        {
            store.RecordOutcome("m-bad", isGood: false, discountFactor: 0.95);
        }

        // 注入 seeded RNG 采样委托，使测试确定性（生产用线程本地未播种 RNG，
        // m-good alpha≈80 >> m-bad beta≈80 时大概率但不 100% m-good 胜出，CI 偶发翻转）。
        var seededRng = new Random(42);
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(),
            store,
            (a, b) => ThompsonSampler.SampleBeta(a, b, seededRng));
        var (ctx, initial) = Setup(options, options.Models, "hi");

        var result = policy.Apply(ctx, initial);

        // 由于采样大概率 m-good 胜出，m-good 应该被重排到最前面，由于初始顺序为 [m-bad, m-good]，发生重排
        Assert.Equal("m-good", result.Candidates[0].Name);
        Assert.Equal("m-bad", result.Candidates[1].Name);
        Assert.Contains("[Thompson Sampling]", result.Reason);
    }
}
