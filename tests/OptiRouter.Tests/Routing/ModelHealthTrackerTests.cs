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
    public void IsCoolingDown_AfterExpiry_ReturnsFalseAndClears()
    {
        var now = DateTime.UtcNow;
        var tracker = new ModelHealthTracker(() => now);

        // 触发熔断，冷却 60s
        for (int i = 0; i < 3; i++)
            tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60);
        Assert.True(tracker.IsCoolingDown("m1"));

        // 推进时钟 61s，冷却到期
        now = now.AddSeconds(61);
        Assert.False(tracker.IsCoolingDown("m1"));

        // 冷却清理后，失败计数也应重置——新的一次失败不再立即触发
        Assert.False(tracker.RecordFailure("m1", threshold: 3, cooldownSeconds: 60));
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
}
