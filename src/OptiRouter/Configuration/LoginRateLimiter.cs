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
/// 内存管理：字典上限 <c>MaxEntries</c>（默认 10000），超限时清扫所有已过期窗口。
/// RecordFailure 每次检查是否需要清扫（O(n) 但仅在超限时触发），防止伪造 X-Forwarded-For 撑爆内存。
/// </remarks>
public sealed class LoginRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly int _maxFailures;
    private readonly TimeSpan _windowDuration;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new(StringComparer.Ordinal);
    private int _cleanupGate;

    /// <summary>默认失败阈值（窗口内 5 次失败即锁定）。</summary>
    public const int DefaultMaxFailures = 5;

    /// <summary>默认统计窗口与锁定时长。</summary>
    public static readonly TimeSpan DefaultWindowDuration = TimeSpan.FromMinutes(5);

    /// <summary>默认字典上限（超出触发清扫）。</summary>
    public const int DefaultMaxEntries = 10000;

    /// <param name="timeProvider">时间源（测试可注入）；默认 <see cref="TimeProvider.System"/>。</param>
    /// <param name="maxFailures">窗口内失败阈值；&lt;=0 用默认。</param>
    /// <param name="windowDuration">统计窗口与锁定时长；null 用默认。</param>
    /// <param name="maxEntries">字典上限（超出触发清扫已过期窗口）；&lt;=0 用默认。</param>
    public LoginRateLimiter(TimeProvider? timeProvider = null, int? maxFailures = null, TimeSpan? windowDuration = null,
        int? maxEntries = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxFailures = maxFailures is > 0 ? maxFailures.Value : DefaultMaxFailures;
        _windowDuration = windowDuration ?? DefaultWindowDuration;
        _maxEntries = maxEntries is > 0 ? maxEntries.Value : DefaultMaxEntries;
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

        // 超限时清扫已过期窗口，防止伪造 IP 撑爆内存。
        TryCleanup(now);
    }

    /// <summary>
    /// 字典超限时清扫所有已过期窗口（含锁定期已过的）。
    /// 用 Interlocked 防并发重复清扫。
    /// </summary>
    private void TryCleanup(DateTimeOffset now)
    {
        if (_windows.Count <= _maxEntries) return;
        if (Interlocked.Exchange(ref _cleanupGate, 1) != 0) return;

        try
        {
            foreach (var kv in _windows)
            {
                // 窗口已过期且不在锁定中 → 可安全删除。
                if (now >= kv.Value.LockedUntil && now - kv.Value.WindowStart >= _windowDuration)
                {
                    _windows.TryRemove(kv.Key, out _);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupGate, 0);
        }
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
