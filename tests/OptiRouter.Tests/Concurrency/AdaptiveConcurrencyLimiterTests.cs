using System.Collections.Concurrent;
using OptiRouter.Concurrency;
using Xunit;

namespace OptiRouter.Tests.Concurrency;

public class AdaptiveConcurrencyLimiterTests
{
    [Fact]
    public async Task AcquireAsync_ReturnsDisposableLease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 10);
        using var lease = await limiter.AcquireAsync("test-model");
        Assert.NotNull(lease);
        Assert.Equal(10, limiter.GetCurrentLimit("test-model"));
    }

    [Fact]
    public void RecordRtt_HighRtt_TriggersMultiplicativeDecrease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 20, backoffFactor: 0.8);
        string model = "gpt-4o";

        // Baseline min RTT
        limiter.RecordRtt(model, 100);
        Assert.Equal(20, limiter.GetCurrentLimit(model));

        // High RTT congestion spike (300ms vs 100ms baseline -> gradient = 100/300 = 0.33 < 0.70)
        limiter.RecordRtt(model, 300);

        int currentLimit = limiter.GetCurrentLimit(model);
        Assert.True(currentLimit < 20, $"Current limit should drop below 20, actual: {currentLimit}");
        Assert.Equal(16, currentLimit); // 20 * 0.8 = 16
    }

    [Fact]
    public void RecordRtt_LowRtt_TriggersAdditiveIncrease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 20, backoffFactor: 0.8);
        string model = "deepseek-chat";

        limiter.RecordRtt(model, 100);
        limiter.RecordRtt(model, 300); // Limit drops to 16

        Assert.Equal(16, limiter.GetCurrentLimit(model));

        // Smooth RTT recovery
        limiter.RecordRtt(model, 105); // gradient = 100 / 105 = 0.95 >= 0.85 -> limit increases to 17

        Assert.Equal(17, limiter.GetCurrentLimit(model));
    }

    [Fact]
    public async Task AcquireAsync_WhenLimitDrops_BlocksNewAcquisitionsUntilSlotsFree()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 1, maxLimit: 10, backoffFactor: 0.5);
        string model = "gpt-4o";

        // 占满 10 个并发槽
        var leases = new List<IDisposable>();
        for (int i = 0; i < 10; i++)
        {
            leases.Add(await limiter.AcquireAsync(model));
        }

        // 拥塞降限：20*0.5... 10*0.5=5
        limiter.RecordRtt(model, 100);
        limiter.RecordRtt(model, 500); // gradient = 0.2 < 0.70 -> 10 * 0.5 = 5
        Assert.Equal(5, limiter.GetCurrentLimit(model));

        // 在飞 10 > 动态上限 5：新获取必须阻塞（真实生效，而非仅数字变化）
        var acquisition = limiter.AcquireAsync(model);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => acquisition.WaitAsync(TimeSpan.FromMilliseconds(200)));

        // 释放 5 个槽（在飞 10->5），仍等于上限，继续阻塞
        for (int i = 0; i < 5; i++)
        {
            leases[i].Dispose();
        }
        await Assert.ThrowsAnyAsync<TimeoutException>(() => acquisition.WaitAsync(TimeSpan.FromMilliseconds(200)));

        // 再释放 1 个（在飞 5->4 < 5）：放行
        leases[5].Dispose();
        using var lease = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_LimitIncrease_WakesUpBlockedWaiters()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 1, maxLimit: 10, backoffFactor: 0.5);
        string model = "deepseek-chat";

        limiter.RecordRtt(model, 100);
        limiter.RecordRtt(model, 500); // 10 * 0.5 = 5
        Assert.Equal(5, limiter.GetCurrentLimit(model));

        // 占满 5 个槽后第 6 个获取阻塞
        var leases = new List<IDisposable>();
        for (int i = 0; i < 5; i++)
        {
            leases.Add(await limiter.AcquireAsync(model));
        }
        var acquisition = limiter.AcquireAsync(model);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => acquisition.WaitAsync(TimeSpan.FromMilliseconds(200)));

        // RTT 恢复 -> 上限 5->6，必须主动唤醒等待者（仅靠完成释放通知会饥饿）
        limiter.RecordRtt(model, 105); // gradient = 100/105 = 0.95 >= 0.85 -> 6
        Assert.Equal(6, limiter.GetCurrentLimit(model));

        using var lease = await acquisition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_ConcurrentAcquisitions_AllEventuallySucceedUnderLimit()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 1, maxLimit: 5, backoffFactor: 0.5);
        string model = "claude-3.5";

        // 降限到 3，并发 20 个获取者排队，最终全部拿到租约（无请求丢失/死锁）
        limiter.RecordRtt(model, 100);
        limiter.RecordRtt(model, 600); // 5 * 0.5 = 2 -> max(1, 2) = 2
        Assert.Equal(2, limiter.GetCurrentLimit(model));

        var leases = new ConcurrentBag<IDisposable>();
        int acquired = 0;
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var lease = await limiter.AcquireAsync(model).WaitAsync(TimeSpan.FromSeconds(5));
            Interlocked.Increment(ref acquired);
            leases.Add(lease);
            await Task.Delay(10); // 模拟处理时间，制造排队
            lease.Dispose();
        }).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(20, acquired);
        Assert.Equal(2, limiter.GetCurrentLimit(model));
    }
}
