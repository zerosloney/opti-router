using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// regenerate 负反馈跟踪器：记录"规范化请求键 → 最近一次完成结果"，同键请求在窗口内
/// 再次到达且上次为成功时，判定为用户 regenerate（对上次答案不满意），供调用方给上次
/// 命中的模型注入低 reward。零额外调用的质量信号，弥补学习状态"只看延迟/失败、看不见答案质量"的缺口。
/// </summary>
/// <remarks>
/// intentional-simple: 进程内字典 + 周期清扫，无持久化（regenerate 信号跨重启丢失可接受）。
/// Fusion/Race 等旁路路径的成败不经过本跟踪器——它们不写入成功记录，因此不会产生误判，
/// 只是漏报信号。键与响应缓存同源（<see cref="ResponseCacheKey.Compute"/>）。
/// </remarks>
public sealed class RegenerateFeedbackTracker
{
    private sealed record Entry(string Model, bool Success, DateTimeOffset TimestampUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _evictionLock = new();
    private DateTimeOffset _lastEvictionUtc;

    /// <summary>软上限：超过后停止写入新键，防止无界增长（与 ResponseCache 语义一致）。</summary>
    private const int MaxEntries = 10_000;

    /// <summary>清扫间隔：窗口外的过期条目按此周期批量淘汰。</summary>
    private static readonly TimeSpan EvictionInterval = TimeSpan.FromMinutes(5);

    public RegenerateFeedbackTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastEvictionUtc = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// 原子消费一次 regenerate 信号：同键上次结果为成功且距今在窗口内 → 返回 true 并移除条目
    /// （一次性消费，当前请求的最终结果稍后经 <see cref="Record"/> 写回）。
    /// 上次为失败（客户端失败重试）或已超窗口（隔天重发同一问题）→ 不判为 regenerate。
    /// </summary>
    /// <param name="key">规范化请求键。</param>
    /// <param name="window">判定窗口。</param>
    /// <param name="model">上次成功命中的模型名（判定成立时有效）。</param>
    public bool TryConsumeRegenerate(string key, TimeSpan window, out string model)
    {
        model = string.Empty;
        if (string.IsNullOrEmpty(key))
            return false;

        TryEvictExpired(window);

        if (!_entries.TryRemove(key, out var entry) || entry is null)
            return false;
        if (!entry.Success)
            return false;
        if (_timeProvider.GetUtcNow() - entry.TimestampUtc > window)
            return false;

        model = entry.Model;
        return true;
    }

    /// <summary>
    /// 记录某键最近一次完成结果。key 为 null（功能未启用）时静默跳过；
    /// 软上限满时跳过新键写入（已有键仍可更新，TryConsume 的移除腾位可自愈）。
    /// </summary>
    public void Record(string? key, string model, bool success)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model))
            return;

        var entry = new Entry(model, success, _timeProvider.GetUtcNow());
        if (_entries.ContainsKey(key))
        {
            _entries[key] = entry;
            return;
        }
        if (CapacityAvailable())
            _entries.TryAdd(key, entry);
    }

    /// <summary>周期清扫窗口外的过期条目；同一时间只允许一个线程执行。</summary>
    private void TryEvictExpired(TimeSpan window)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_evictionLock)
        {
            if (now - _lastEvictionUtc < EvictionInterval)
                return;
            _lastEvictionUtc = now;
        }

        foreach (var kv in _entries)
        {
            if (now - kv.Value.TimestampUtc > window)
                _entries.TryRemove(kv.Key, out _);
        }
    }

    private bool CapacityAvailable() => _entries.Count < MaxEntries;
}
