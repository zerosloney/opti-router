using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// 登录失败限流器测试：验证阈值锁定、锁定期不延长、窗口过期重置、成功清零与多 IP 独立。
/// </summary>
public class LoginRateLimiterTests
{
    [Fact]
    public void Failures_UnderThreshold_DoNotLock()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 4; i++)
            limiter.RecordFailure("1.2.3.4");

        Assert.False(limiter.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void Failures_AtThreshold_Lock()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 5; i++)
            limiter.RecordFailure("1.2.3.4");

        Assert.True(limiter.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void Locked_Window_ContinuedFailures_DoNotExtend()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 5; i++)
            limiter.RecordFailure("1.2.3.4");
        Assert.True(limiter.IsLocked("1.2.3.4"));

        // 锁定期内继续失败：不应延长锁定（仍以首次触发锁定时刻 +window 为准）。
        clock.Advance(TimeSpan.FromMinutes(1));
        limiter.RecordFailure("1.2.3.4");
        limiter.RecordFailure("1.2.3.4");

        // 距锁定起点 5 分钟整：锁定到期（第 maxFailures 次失败时刻 +window）。
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(limiter.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void Window_Expires_ResetsCounter()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 4; i++) // 4 次，未锁
            limiter.RecordFailure("1.2.3.4");

        clock.Advance(TimeSpan.FromMinutes(6)); // 窗口过期
        // 新窗口：再 4 次仍不应锁（计数已重置）。
        for (int i = 0; i < 4; i++)
            limiter.RecordFailure("1.2.3.4");

        Assert.False(limiter.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void Reset_ClearsFailures()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 5; i++)
            limiter.RecordFailure("1.2.3.4");
        Assert.True(limiter.IsLocked("1.2.3.4"));

        limiter.Reset("1.2.3.4");
        Assert.False(limiter.IsLocked("1.2.3.4"));
    }

    [Fact]
    public void DifferentIps_CountedIndependently()
    {
        var clock = new MutableTimeProvider();
        var limiter = new LoginRateLimiter(clock, maxFailures: 5, windowDuration: TimeSpan.FromMinutes(5));

        for (int i = 0; i < 5; i++)
            limiter.RecordFailure("attacker");

        Assert.True(limiter.IsLocked("attacker"));
        Assert.False(limiter.IsLocked("other"));
    }

    [Fact]
    public void EmptyOrWhitespaceIp_IsIgnored()
    {
        var limiter = new LoginRateLimiter();
        limiter.RecordFailure("");
        limiter.RecordFailure("   ");

        Assert.False(limiter.IsLocked(""));
        Assert.False(limiter.IsLocked("   "));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
