using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ModelHealthTrackerTests
{
    [Fact]
    public void RecordFailure_BelowThreshold_NotCoolingDown()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // threshold=3，两次失败不触发
        Assert.False(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
        Assert.False(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
        Assert.False(tracker.IsCoolingDown("m1"));
    }

    [Fact]
    public void RecordFailure_AtThreshold_TripsCoolingDown()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        bool tripped = tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);

        Assert.True(tripped);
        Assert.True(tracker.IsCoolingDown("m1"));
    }

    [Fact]
    public void IsCoolingDown_AfterExpiry_TransitionsToHalfOpen_ProbeFailureReopens()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断，冷却 60s
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.True(tracker.IsCoolingDown("m1"));
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 推进时钟 61s，冷却到期 → 半开
        now = now.AddSeconds(61);
        Assert.False(tracker.IsCoolingDown("m1"));
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // 半开状态下的一次失败视为探测失败，立即重新打开熔断
        Assert.True(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));
        Assert.True(tracker.IsCoolingDown("m1"));
    }

    [Fact]
    public void HalfOpen_ProbeSuccess_ClosesCircuit()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断 → 打开
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 冷却到期 → 半开，放行一个探测
        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 1));

        // 探测成功 → 闭合，失败计数清零
        tracker.RecordSuccess("m1");
        Assert.Equal(CircuitState.Closed, tracker.GetState("m1"));
        Assert.False(tracker.IsCoolingDown("m1"));

        // 闭合后单次失败不再立即触发
        Assert.False(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
    }

    [Fact]
    public void TryBeginProbe_Closed_ReturnsTrue()
    {
        var tracker = new ModelHealthTracker();
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 1));
    }

    [Fact]
    public void TryBeginProbe_Open_ReturnsFalse()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 打开（冷却中）不放行探测
        Assert.False(tracker.TryBeginProbe("m1", maxProbes: 1));
    }

    [Fact]
    public void TryBeginProbe_HalfOpen_LimitedByMaxProbes()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断 → 打开 → 冷却到期 → 半开
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // maxProbes=2：前两个探测放行，第三个被拒
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 2));
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 2));
        Assert.False(tracker.TryBeginProbe("m1", maxProbes: 2));

        // 释放一个探测槽位后，可再放行一个
        tracker.ReleaseProbe("m1");
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 2));
    }

    [Fact]
    public void TryBeginProbe_HalfOpen_SuccessReleasesSlot()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // maxProbes=1：放行一个探测；成功上报后闭合，槽位随之释放
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 1));
        Assert.False(tracker.TryBeginProbe("m1", maxProbes: 1));
        tracker.RecordSuccess("m1");
        Assert.Equal(CircuitState.Closed, tracker.GetState("m1"));
    }

    [Fact]
    public void ReleaseProbe_NoProbeTaken_NoOp()
    {
        var tracker = new ModelHealthTracker();
        // 闭合态下释放探测不抛异常、不改变状态
        tracker.ReleaseProbe("m1");
        Assert.Equal(CircuitState.Closed, tracker.GetState("m1"));
    }

    [Fact]
    public void GetState_UnknownModel_ReturnsClosed()
    {
        var tracker = new ModelHealthTracker();
        Assert.Equal(CircuitState.Closed, tracker.GetState("never-seen"));
    }

    [Fact]
    public void RecordFailure_InFlightProbeFailure_RefreshesCooldown()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断 → 打开，冷却 60s
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);

        // 推进 30s 后一个在途请求迟到失败：仍是打开态，刷新冷却到期
        now = now.AddSeconds(30);
        Assert.True(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 只推进 30s（不足刷新后的 60s 冷却）仍应处于冷却
        now = now.AddSeconds(30);
        Assert.True(tracker.IsCoolingDown("m1"));
    }

    [Fact]
    public void RecordSuccess_ClearsCountAndCooldown()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.True(tracker.IsCoolingDown("m1"));

        tracker.RecordSuccess("m1");
        Assert.False(tracker.IsCoolingDown("m1"));

        // 计数清零，需再次累计达阈值才触发
        Assert.False(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
    }

    [Fact]
    public void IsCoolingDown_UnknownModel_ReturnsFalse()
    {
        var tracker = new ModelHealthTracker();
        Assert.False(tracker.IsCoolingDown("never-seen"));
    }

    [Fact]
    public void RecordFailure_ThresholdZero_NeverTrips()
    {
        var tracker = new ModelHealthTracker();
        Assert.False(tracker.RecordFailure("m1", threshold: 0, cooldownSeconds: 60));
        Assert.False(tracker.IsCoolingDown("m1"));
    }

    [Fact]
    public void RecordFailure_EmptyName_NoOp()
    {
        var tracker = new ModelHealthTracker();
        Assert.False(tracker.RecordFailure("", threshold: 1, cooldownSeconds: 60));
        Assert.False(tracker.IsCoolingDown(""));
    }

    [Fact]
    public void HalfOpen_RequiredSuccesses_StaysHalfOpenUntilThresholdMet()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断 → 打开
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 冷却到期 → 半开
        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // requiredSuccesses=3：前两次成功保持半开
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // 第三次成功 → 闭合
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.Equal(CircuitState.Closed, tracker.GetState("m1"));
    }

    [Fact]
    public void HalfOpen_RequiredSuccesses_FailureResetsCounterAndReopens()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // 两次成功（未达阈值 3）
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));

        // 第三次探测失败 → 重开，成功计数清零
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        Assert.True(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
        Assert.Equal(CircuitState.Open, tracker.GetState("m1"));

        // 再次进入半开后，需重新累计 3 次（非延续之前的 2 次）
        now = now.AddSeconds(61);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));
        Assert.True(tracker.TryBeginProbe("m1", maxProbes: 3));
        tracker.RecordSuccess("m1", requiredSuccesses: 3);
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("m1"));
    }
}
