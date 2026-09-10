using OptiRouter.Concurrency;
using Xunit;

namespace OptiRouter.Tests.Concurrency;

/// <summary>
/// P2-5 回归：并发注册表租约化。修复前返回裸 SemaphoreSlim，"引用—Wait—可淘汰判定"
/// 分离——拿引用尚未 Wait 时条目可被空闲扫描按 CurrentCount==Initial 判为空闲淘汰，
/// 同分区随后重建第二道闸，实际并发超过单闸上限；配置替换竞争失败还会回退旧 entry
/// 让旧限制继续生效。租约化后：等待在注册表内完成，活跃租约期间条目绝不淘汰。
/// </summary>
public sealed class ConcurrencyRegistryTests
{
    private static ConcurrencyRegistry CreateRegistry() =>
        new(idleEvictionInterval: TimeSpan.FromMilliseconds(60), sweepInterval: TimeSpan.FromMilliseconds(10));

    [Fact]
    public void Acquire_LimitsConcurrency_AndReleasesOnDispose()
    {
        var registry = CreateRegistry();

        using var first = registry.Acquire("p", maxConcurrency: 1);
        Assert.NotNull(first);
        Assert.Null(registry.Acquire("p", 1)); // 分区已满 → 429 路径

        first.Dispose();
        using var second = registry.Acquire("p", 1);
        Assert.NotNull(second); // 释放后可再次进入
    }

    /// <summary>活跃租约阻止淘汰：超过淘汰年龄后同分区仍是同一道闸（修复前此窗口产生双闸）。</summary>
    [Fact]
    public async Task ActiveLease_PreventsEviction_SameGateKeepsEnforcing()
    {
        var registry = CreateRegistry();

        using var held = registry.Acquire("p", 1);
        Assert.NotNull(held);

        // 跨过淘汰年龄，持续用第 429 路径的 Acquire 触发扫描。
        for (int i = 0; i < 20; i++)
        {
            Assert.Null(registry.Acquire("p", 1)); // 若条目被淘汰重建，这里会拿到新闸返回非 null
            await Task.Delay(20);
        }

        held.Dispose();
        using var after = registry.Acquire("p", 1);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task IdleEntry_IsEvicted_AfterIdleAge()
    {
        var registry = CreateRegistry();

        var lease = registry.Acquire("gone", 1);
        Assert.NotNull(lease);
        lease.Dispose();

        // 持续在另一分区触发扫描，直至 gone 条目超龄被移除。
        for (int i = 0; i < 30 && registry.TrackedPartitionCount > 1; i++)
        {
            using var trigger = registry.Acquire("other", 1);
            await Task.Delay(20);
        }

        Assert.Equal(1, registry.TrackedPartitionCount); // 只剩触发用分区
    }

    [Fact]
    public void ConfigReplacement_NewLimitApplies_HeldLeaseUnaffected()
    {
        var registry = CreateRegistry();

        var old = registry.Acquire("p", 1);
        Assert.NotNull(old);

        // 新限制 3：新条目独立计数，3 个名额可全部进入；旧租约不占新闸名额。
        var a = registry.Acquire("p", 3);
        var b = registry.Acquire("p", 3);
        var c = registry.Acquire("p", 3);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.Null(registry.Acquire("p", 3)); // 新闸已满

        old.Dispose(); // 旧条目租约排空，不影响新条目计数
        Assert.Null(registry.Acquire("p", 3));

        a.Dispose();
        using var d = registry.Acquire("p", 3);
        Assert.NotNull(d);
    }

    /// <summary>并发压入：同分区分区闸在多线程交错下不超限（屏障式压力）。</summary>
    [Fact]
    public async Task ConcurrentAcquire_NeverExceedsLimit()
    {
        var registry = CreateRegistry();
        const int limit = 2;
        int inFlight = 0, maxObserved = 0;

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            using var lease = registry.Acquire("p", limit);
            if (lease is null) return;
            int now = Interlocked.Increment(ref inFlight);
            int priorMax = Volatile.Read(ref maxObserved);
            while (now > priorMax && Interlocked.CompareExchange(ref maxObserved, now, priorMax) != priorMax)
                priorMax = Volatile.Read(ref maxObserved);
            Thread.SpinWait(200);
            Interlocked.Decrement(ref inFlight);
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.True(maxObserved <= limit, $"observed concurrency {maxObserved} > limit {limit}");
    }
}
