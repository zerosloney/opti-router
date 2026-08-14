using System.Collections.Concurrent;

namespace OptiRouter.Configuration;

/// <summary>
/// 管理端登录失败内存限流器：按客户端 IP 累计失败次数，超过阈值后临时锁定该 IP，防字典爆破。
/// 单实例内存（重启清零），适用于管理台这种低频登录场景——多实例部署下各实例独立计数。
/// </summary>
/// <remarks>
/// 锁定语义：在 <c>WindowDuration</c> 窗口内累计失败达 <c>MaxFailures</c> 次，即锁定 <c>WindowDuration</c>。
/// 锁定窗口内的后续失败不延长锁定、不计数（避免持续失败造成永久锁定）；窗口过期后重新计数。成功登录即清除该 IP 记录。
/// </remarks>
/// <remarks>
/// intentional-simple: 失败记录按 IP 累积，无主动清理（仅成功登录才删除）。管理台登录低频、记录极小、
/// 进程重启即清零；若未来面临海量不同 IP 的分布式爆破，改为 LRU 上限或定期清扫已过期窗口。
/// </remarks>
public sealed class LoginRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly int _maxFailures;
    private readonly TimeSpan _windowDuration;
    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>默认失败阈值（窗口内 5 次失败即锁定）。</summary>
    public const int DefaultMaxFailures = 5;

    /// <summary>默认统计窗口与锁定时长。</summary>
    public static readonly TimeSpan DefaultWindowDuration = TimeSpan.FromMinutes(5);

    /// <param name="timeProvider">时间源（测试可注入）；默认 <see cref="TimeProvider.System"/>。</param>
    /// <param name="maxFailures">窗口内失败阈值；&lt;=0 用默认。</param>
    /// <param name="windowDuration">统计窗口与锁定时长；null 用默认。</param>
    public LoginRateLimiter(TimeProvider? timeProvider = null, int? maxFailures = null, TimeSpan? windowDuration = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxFailures = maxFailures is > 0 ? maxFailures.Value : DefaultMaxFailures;
        _windowDuration = windowDuration ?? DefaultWindowDuration;
    }

    /// <summary>该 IP 是否处于锁定窗口。</summary>
    public bool IsLocked(string clientIp)
    {
        if (string.IsNullOrEmpty(clientIp)) return false;
        return _windows.TryGetValue(clientIp, out var w) && _timeProvider.GetUtcNow() < w.LockedUntil;
    }

    /// <summary>记录一次失败。空 IP 忽略（无法归因）。</summary>
    public void RecordFailure(string clientIp)
    {
        if (string.IsNullOrEmpty(clientIp)) return;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _windows.AddOrUpdate(
            clientIp,
            _ => NewWindow(now, count: 1),
            (_, existing) => now < existing.LockedUntil
                ? existing // 锁定期内：不延长、不计数。
                : (now - existing.WindowStart >= _windowDuration
                    ? NewWindow(now, count: 1) // 统计窗口过期：重置开新窗口。
                    : NewWindow(existing.WindowStart, existing.Count + 1))); // 窗口内累计。
    }

    /// <summary>登录成功后清除该 IP 的失败记录。</summary>
    public void Reset(string clientIp)
    {
        if (!string.IsNullOrEmpty(clientIp))
            _windows.TryRemove(clientIp, out _);
    }

    private FailureWindow NewWindow(DateTimeOffset start, int count) =>
        new(start, count, count >= _maxFailures ? start + _windowDuration : DateTimeOffset.MinValue);

    private sealed record FailureWindow(DateTimeOffset WindowStart, int Count, DateTimeOffset LockedUntil);
}
