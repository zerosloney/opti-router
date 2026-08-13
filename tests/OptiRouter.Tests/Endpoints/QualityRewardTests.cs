using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 质量驱动 reward 通道测试。验证 <see cref="OutcomeRecorder.RecordQualityOutcome"/> 把显式质量 reward
/// 正确注入 Thompson 采样状态（而非延迟映射），修复"质量信号被丢弃、学习状态只看快慢"的缺陷。
/// </summary>
public sealed class QualityRewardTests
{
    private static OutcomeRecorder CreateRecorder(ThompsonStateStore tsStore, RouterOptions? options = null)
    {
        var monitor = new FakeRouterOptionsMonitor(options ?? new RouterOptions());
        return new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: monitor,
            affinityCache: null!,
            tsStore: tsStore,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance);
    }

    [Fact]
    public void LowQualityReward_PenalizesModel_InThompsonState()
    {
        // reward 0.0 = 答案质量差 → Beta（不佳次数）应增长，Alpha（良好次数）应收缩。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordQualityOutcome("cheap-model", 0.0);

        var stats = tsStore.GetOrAdd("cheap-model");
        // 初始 Alpha=1, Beta=1, discount=0.95: Alpha=1*0.95+0=0.95, Beta=1*0.95+(1-0)=1.95
        Assert.Equal(0.95, stats.Alpha, precision: 3);
        Assert.Equal(1.95, stats.Beta, precision: 3);
    }

    [Fact]
    public void HighQualityReward_RewardsModel_InThompsonState()
    {
        // reward 1.0 = 答案质量高 → Alpha（良好次数）应增长，Beta 应收缩。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordQualityOutcome("cheap-model", 1.0);

        var stats = tsStore.GetOrAdd("cheap-model");
        // Alpha=1*0.95+1=1.95, Beta=1*0.95+0=0.95
        Assert.Equal(1.95, stats.Alpha, precision: 3);
        Assert.Equal(0.95, stats.Beta, precision: 3);
    }

    [Fact]
    public void OutOfRangeReward_IsClampedToUnitInterval()
    {
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        // 5.0 应 clamp 到 1.0（等同高质量正反馈）
        recorder.RecordQualityOutcome("model-high", 5.0);
        // -3.0 应 clamp 到 0.0（等同低质量负反馈）
        recorder.RecordQualityOutcome("model-low", -3.0);

        var high = tsStore.GetOrAdd("model-high");
        var low = tsStore.GetOrAdd("model-low");
        Assert.Equal(1.95, high.Alpha, precision: 3);   // clamp(5.0)=1.0
        Assert.Equal(0.95, low.Alpha, precision: 3);    // clamp(-3.0)=0.0
    }

    [Fact]
    public void QualityReward_DistinctFromLatencyReward()
    {
        // 同一模型：先快成功（延迟 reward=1.0），再质量负反馈（reward=0.0）。
        // 两者都应作用于同一 Beta 分布，质量负反馈应抵消部分延迟正反馈。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordThompsonOutcome("cheap-model", elapsedMs: 100); // 快成功 → reward 1.0
        recorder.RecordQualityOutcome("cheap-model", 0.0);             // 质量差 → reward 0.0

        var stats = tsStore.GetOrAdd("cheap-model");
        // 第一次 reward 1.0: Alpha=1*0.95+1=1.95, Beta=1*0.95+0=0.95
        // 第二次 reward 0.0: Alpha=1.95*0.95+0=1.8525, Beta=0.95*0.95+1=1.9025
        Assert.Equal(1.8525, stats.Alpha, precision: 4);
        Assert.Equal(1.9025, stats.Beta, precision: 4);
    }
}
