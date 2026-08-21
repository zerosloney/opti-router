using OptiRouter.Clients;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public sealed class CalibratingTokenEstimatorTests
{
    /// <summary>
    /// 固定内层估算器（返回常量），隔离校准逻辑本身。
    /// </summary>
    private sealed class FixedEstimator(int value) : ITokenEstimator
    {
        public int Estimate(ChatRequest request) => value;
    }

    private static ChatRequest EmptyRequest => new() { Messages = new List<ChatMessage>() };

    [Fact]
    public void WithoutObservations_EstimateEqualsInner()
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(1000));

        Assert.Equal(1000, estimator.Estimate(EmptyRequest));
        Assert.Equal(1.0, estimator.CurrentRatio);
    }

    [Fact]
    public void Observe_LowerActual_ReducesEstimate()
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(1000));

        // 实际 500 / 估算 1000 = 0.5，暖身期 α=0.5 → ratio ≈ 0.75
        estimator.Observe(1000, 500);

        Assert.Equal(750, estimator.Estimate(EmptyRequest));
    }

    [Fact]
    public void Observe_ConvergesTowardObservedRatio()
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(1000));

        // 连续观测同一比值 0.6：暖身后 ratio 应显著逼近 0.6
        for (int i = 0; i < 30; i++)
            estimator.Observe(1000, 600);

        Assert.InRange(estimator.CurrentRatio, 0.59, 0.61);
    }

    [Fact]
    public void Observe_ClampsRatioIntoSafeBounds()
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(1000));

        // 反复观测极端比值 10（真实 token 远高于估算），比率不得突破上限 3.0
        for (int i = 0; i < 50; i++)
            estimator.Observe(1000, 10_000);

        Assert.Equal(3.0, estimator.CurrentRatio);
        Assert.Equal(3000, estimator.Estimate(EmptyRequest));
    }

    [Theory]
    [InlineData(0)]      // 估算为 0：非法样本
    [InlineData(100)]    // 实际 token 太小：噪声样本
    [InlineData(50)]     // 比值 0.05 < 0.1：异常样本
    public void Observe_InvalidSamples_AreIgnored(int actual)
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(1000));

        estimator.Observe(1000, actual);

        Assert.Equal(1.0, estimator.CurrentRatio);
        Assert.Equal(0, estimator.Observations);
    }

    [Fact]
    public void Estimate_ZeroInnerEstimate_ReturnsZero()
    {
        var estimator = new CalibratingTokenEstimator(new FixedEstimator(0));

        Assert.Equal(0, estimator.Estimate(EmptyRequest));
    }
}
