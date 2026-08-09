using System;
using System.Collections.Generic;
using System.Linq;
using OptiRouter.Clients;
using OptiRouter.Configuration;
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

        var policy = new LatencyAwarePolicy(new StubLatencyStatsProvider(), store);
        var (ctx, initial) = Setup(options, options.Models, "hi");

        var result = policy.Apply(ctx, initial);

        // 由于采样大概率 m-good 胜出，m-good 应该被重排到最前面，由于初始顺序为 [m-bad, m-good]，发生重排
        Assert.Equal("m-good", result.Candidates[0].Name);
        Assert.Equal("m-bad", result.Candidates[1].Name);
        Assert.Contains("[Thompson Sampling]", result.Reason);
    }
}
