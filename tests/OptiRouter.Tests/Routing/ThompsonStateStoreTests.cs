using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ThompsonStateStoreTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    [Fact]
    public void RecordOutcome_IncrementsN_AndUpdatesLastUpdateUtc()
    {
        var store = new ThompsonStateStore();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        store.RecordOutcome("model-a", true, discountFactor: 1.0);
        var stats = store.GetOrAdd("model-a");

        Assert.Equal(1, stats.N);
        Assert.NotEqual(DateTimeOffset.MinValue, stats.LastUpdateUtc);
        Assert.True(stats.LastUpdateUtc >= before);
        Assert.Equal(2.0, stats.Alpha); // 先验 1.0 + 1.0
        Assert.Equal(1.0, stats.Beta);  // 先验 1.0 + 0.0
    }

    [Fact]
    public void RecordOutcome_MultipleUpdates_AccumulateN_AndRefreshLastUpdateUtc()
    {
        var store = new ThompsonStateStore();

        store.RecordOutcome("model-a", true, discountFactor: 1.0);
        var first = store.GetOrAdd("model-a").LastUpdateUtc;

        // 短暂等待确保时间推进。
        Thread.Sleep(10);
        store.RecordOutcome("model-a", false, discountFactor: 1.0);
        var stats = store.GetOrAdd("model-a");

        Assert.Equal(2, stats.N);
        Assert.True(stats.LastUpdateUtc >= first);
        Assert.Equal(2.0, stats.Alpha); // 1.0 + 1.0
        Assert.Equal(2.0, stats.Beta);  // 1.0 + 1.0
    }

    [Fact]
    public void RecordOutcome_UsesInjectedTimeProvider()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new ThompsonStateStore(timeProvider: time);

        store.RecordOutcome("model-a", true, discountFactor: 1.0);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), store.GetOrAdd("model-a").LastUpdateUtc);

        time.Advance(TimeSpan.FromMinutes(5));
        store.RecordOutcome("model-a", false, discountFactor: 1.0);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:05:00Z"), store.GetOrAdd("model-a").LastUpdateUtc);
    }

    [Fact]
    public void GetSnapshot_SortsByNDescending()
    {
        var store = new ThompsonStateStore();

        store.RecordOutcome("cold", true, discountFactor: 1.0); // N=1
        store.RecordOutcome("hot", true, discountFactor: 1.0);
        store.RecordOutcome("hot", true, discountFactor: 1.0); // N=2
        store.RecordOutcome("warm", true, discountFactor: 1.0);
        store.RecordOutcome("warm", true, discountFactor: 1.0);
        store.RecordOutcome("warm", true, discountFactor: 1.0); // N=3

        var snapshot = store.GetSnapshot();

        Assert.Equal(["warm", "hot", "cold"], snapshot.Select(s => s.Model).ToList());
        Assert.Equal(3, snapshot[0].N);
        Assert.Equal(2, snapshot[1].N);
        Assert.Equal(1, snapshot[2].N);
    }

    [Fact]
    public void GetSnapshot_IncludesAlphaBetaAndLastUpdateUtc()
    {
        var store = new ThompsonStateStore();
        store.RecordOutcome("model-a", true, discountFactor: 1.0);

        var snapshot = store.GetSnapshot();
        var s = Assert.Single(snapshot);

        Assert.Equal("model-a", s.Model);
        Assert.Equal(2.0, s.Alpha);
        Assert.Equal(1.0, s.Beta);
        Assert.Equal(1, s.N);
        Assert.NotEqual(DateTimeOffset.MinValue, s.LastUpdateUtc);
        Assert.Equal(0.6666666666666666, s.Mean, precision: 10);
    }

    [Fact]
    public void GetSnapshot_EmptyStore_ReturnsEmptyList()
    {
        var store = new ThompsonStateStore();
        Assert.Empty(store.GetSnapshot());
    }

    [Fact]
    public void RecordOutcome_DiscountFactor_AttenuatesHistory()
    {
        var store = new ThompsonStateStore();

        // factor=1.0：历史无衰减。
        store.RecordOutcome("model-a", true, discountFactor: 1.0);
        store.RecordOutcome("model-a", true, discountFactor: 1.0);
        var full = store.GetOrAdd("model-a");
        Assert.Equal(3.0, full.Alpha);
        Assert.Equal(1.0, full.Beta);

        // factor=0.5：历史衰减。
        store.RecordOutcome("model-b", true, discountFactor: 0.5);
        store.RecordOutcome("model-b", true, discountFactor: 0.5);
        var attenuated = store.GetOrAdd("model-b");
        // 第一次：alpha = 1.0*0.5 + 1.0 = 1.5; beta = 1.0*0.5 + 0.0 = 0.5
        // 第二次：alpha = 1.5*0.5 + 1.0 = 1.75; beta = 0.5*0.5 + 0.0 = 0.25
        Assert.Equal(1.75, attenuated.Alpha);
        Assert.Equal(0.25, attenuated.Beta);
        Assert.Equal(2, attenuated.N);
    }
}
