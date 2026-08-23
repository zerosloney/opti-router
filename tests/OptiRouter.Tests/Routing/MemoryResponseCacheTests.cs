using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 响应缓存字节预算：缓存的是完整响应体，MB 级大响应 × 条目数上限会无界吃内存——
/// 按 UTF-8 体量估算做软上限，超预算拒写、淘汰归还。
/// </summary>
public class MemoryResponseCacheTests
{
    private static RawChatResponse Resp(string body) => new(body, null);

    [Fact]
    public void Set_BeyondByteBudget_RejectedEvenUnderEntryCap()
    {
        using var mc = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryResponseCache(mc, maxEntries: 10, maxBytes: 1024);

        cache.Set("big", Resp(new string('a', 2048)), TimeSpan.FromMinutes(1));

        Assert.False(cache.TryGet("big", out _));
        var stats = cache.GetStats();
        Assert.Equal(0, stats.CurrentEntries);
        Assert.Equal(0, stats.CurrentBytes);
    }

    [Fact]
    public void Set_WithinBudget_AccumulatesBytes_AndEvictionReturnsThem()
    {
        using var mc = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryResponseCache(mc, maxEntries: 10, maxBytes: 100_000);

        cache.Set("k1", Resp(new string('b', 1000)), TimeSpan.FromMilliseconds(50));

        var afterSet = cache.GetStats();
        Assert.Equal(1, afterSet.CurrentEntries);
        Assert.True(afterSet.CurrentBytes > 1000); // Body 字节 + 固定开销

        // TTL 过期后的访问触发淘汰回调（异步派发），归还的字节数使后续写入重新获得预算。
        Assert.True(WaitUntil(() =>
        {
            cache.TryGet("k1", out _);
            return cache.GetStats().CurrentBytes == 0;
        }, TimeSpan.FromSeconds(5)), "eviction should return byte budget");
    }

    [Fact]
    public void Set_MaxBytesZero_IsUnlimited()
    {
        using var mc = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryResponseCache(mc, maxEntries: 10, maxBytes: 0);

        cache.Set("big", Resp(new string('c', 100_000)), TimeSpan.FromMinutes(1));

        Assert.True(cache.TryGet("big", out _));
        Assert.Equal(0, cache.GetStats().MaxBytes);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
