using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RegenerateFeedbackTrackerTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    [Fact]
    public void TryConsumeRegenerate_SuccessWithinWindow_ReturnsTrueAndRemovesEntry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new RegenerateFeedbackTracker(time);

        tracker.Record("key-1", "model-a", success: true);
        bool consumed = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out string model);

        Assert.True(consumed);
        Assert.Equal("model-a", model);
        // 一次性消费：同一 key 再次消费应失败。
        bool second = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out _);
        Assert.False(second);
    }

    [Fact]
    public void TryConsumeRegenerate_LastFailure_ReturnsFalse()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new RegenerateFeedbackTracker(time);

        tracker.Record("key-1", "model-a", success: false);
        bool consumed = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out _);

        Assert.False(consumed);
    }

    [Fact]
    public void TryConsumeRegenerate_OutsideWindow_ReturnsFalse()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new RegenerateFeedbackTracker(time);

        tracker.Record("key-1", "model-a", success: true);
        time.Advance(TimeSpan.FromHours(2)); // 超过 1 小时窗口

        bool consumed = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out _);
        Assert.False(consumed);
    }

    [Fact]
    public void TryConsumeRegenerate_MissingKey_ReturnsFalse()
    {
        var tracker = new RegenerateFeedbackTracker();
        bool consumed = tracker.TryConsumeRegenerate("nonexistent", TimeSpan.FromHours(1), out _);
        Assert.False(consumed);
    }

    [Fact]
    public void TryConsumeRegenerate_NullOrEmptyKey_ReturnsFalse()
    {
        var tracker = new RegenerateFeedbackTracker();

        Assert.False(tracker.TryConsumeRegenerate(null!, TimeSpan.FromHours(1), out _));
        Assert.False(tracker.TryConsumeRegenerate(string.Empty, TimeSpan.FromHours(1), out _));
    }

    [Fact]
    public void Record_NullOrEmptyKey_NoOp()
    {
        var tracker = new RegenerateFeedbackTracker();

        tracker.Record(null, "model-a", success: true);
        tracker.Record(string.Empty, "model-a", success: true);
        tracker.Record("key-1", null!, success: true);
        tracker.Record("key-1", string.Empty, success: true);

        bool consumed = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out _);
        Assert.False(consumed);
    }

    [Fact]
    public void Record_SoftCap_StopsNewEntriesAfterMax()
    {
        var tracker = new RegenerateFeedbackTracker();
        int max = 10_000;

        for (int i = 0; i < max; i++)
        {
            tracker.Record($"key-{i}", "model-a", success: true);
        }

        // 超出软上限后，新键不应被写入。
        tracker.Record("key-overflow", "model-a", success: true);
        bool consumed = tracker.TryConsumeRegenerate("key-overflow", TimeSpan.FromHours(1), out _);
        Assert.False(consumed);

        // 已有键仍可更新。
        tracker.Record("key-0", "model-b", success: false);
        bool consumedFail = tracker.TryConsumeRegenerate("key-0", TimeSpan.FromHours(1), out _);
        Assert.False(consumedFail); // 上次为失败，不判定 regenerate
    }

    [Fact]
    public void TryEvictExpired_RemovesEntriesOutsideWindow()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new RegenerateFeedbackTracker(time);

        tracker.Record("key-1", "model-a", success: true);
        tracker.Record("key-2", "model-a", success: true);

        // 先消费一个，确认正常。
        Assert.True(tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out _));

        // 推进到 eviction 间隔之外，并把 key-2 推到窗口外。
        time.Advance(TimeSpan.FromMinutes(6));
        time.Advance(TimeSpan.FromHours(2));

        // 再次消费 key-2，触发 eviction。
        bool consumed = tracker.TryConsumeRegenerate("key-2", TimeSpan.FromHours(1), out _);
        Assert.False(consumed);

        // key-1 已被消费移除；key-2 因超时被 evict，后续消费也失败。
        bool again = tracker.TryConsumeRegenerate("key-2", TimeSpan.FromHours(1), out _);
        Assert.False(again);
    }

    [Fact]
    public void Record_UpdatesExistingKey()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new RegenerateFeedbackTracker(time);

        tracker.Record("key-1", "model-a", success: true);
        time.Advance(TimeSpan.FromMinutes(30));
        tracker.Record("key-1", "model-b", success: true);

        bool consumed = tracker.TryConsumeRegenerate("key-1", TimeSpan.FromHours(1), out string model);
        Assert.True(consumed);
        Assert.Equal("model-b", model);
    }
}
