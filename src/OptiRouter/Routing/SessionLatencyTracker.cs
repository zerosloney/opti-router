using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 会话级延迟窗口跟踪器：为 <see cref="SessionAffinityPolicy"/> 的"延迟熔断"逃生通道提供数据。
/// 内存内 ConcurrentDictionary + 每 session 一个环形 buffer，零依赖（环形 buffer 自身有锁，
/// 读写均只持有短锁）。
/// </summary>
/// <remarks>
/// intentional-simple: 不持久化（重启清空）。过期 session 在后续写入时最多每分钟清扫一次；
/// 若需要空闲进程也立即归还内存，再升级为定时后台清扫。环形 buffer 按当前逃生窗口大小保留最近记录；
/// 配置调大时扩容，调小时保留已有容量但只统计新窗口。
/// </remarks>
public sealed class SessionLatencyTracker
{
    private const int DefaultWindowSize = 16;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, LatencyEntry> _rings = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private long _nextCleanupTicks;

    public SessionLatencyTracker()
        : this(TimeProvider.System)
    {
    }

    internal SessionLatencyTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    internal int EntryCount => _rings.Count;

    /// <summary>
    /// 记录一次成功请求的延迟。latencyMs &lt;= 0 视为无效（路由前决策或异常路径），不写入。
    /// </summary>
    public void Record(string? sessionId, long latencyMs)
        => Record(sessionId, latencyMs, DefaultWindowSize, DefaultTtl);

    internal void Record(string? sessionId, long latencyMs, int windowSize, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(sessionId) || latencyMs <= 0) return;
        windowSize = Math.Max(1, windowSize);
        var now = _timeProvider.GetUtcNow();
        CleanupExpired(now);
        long expiresAtTicks = now.Add(ttl).UtcDateTime.Ticks;
        var entry = _rings.AddOrUpdate(
            sessionId,
            _ => new LatencyEntry(new LatencyRing(windowSize), expiresAtTicks),
            (_, existing) => new LatencyEntry(existing.Ring, expiresAtTicks));
        entry.Ring.Push(latencyMs, windowSize);
    }

    /// <summary>
    /// 读取最近 N 次成功请求的平均延迟。session 不存在时返回 false（avg=0）。
    /// 调用方可指定 <paramref name="minSamples"/>：仅当 ring 内已有样本数 &gt;= 该值时返回 true，
    /// 避免"首次访问"或"样本未达窗口"时给出统计意义不强的 avg。
    /// 显式指定 N（N &gt; 1）时仅统计最近 N 次记录，不在范围内（out-of-window）的延迟不影响 avg。
    /// 保留默认值 1 的既有兼容语义：未指定窗口时统计 ring 内全部已有样本。
    /// </summary>
    public bool TryGetRecentAverage(string? sessionId, out double avgMs, int minSamples = 1)
    {
        avgMs = 0;
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_rings.TryGetValue(sessionId, out var entry) || entry is null) return false;
        if (entry.ExpiresAtTicks <= _timeProvider.GetUtcNow().UtcDateTime.Ticks)
        {
            RemoveExact(sessionId, entry);
            return false;
        }
        return entry.Ring.TryGetAverage(out avgMs, minSamples);
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        long nowTicks = now.UtcDateTime.Ticks;
        long nextCleanup = Volatile.Read(ref _nextCleanupTicks);
        if (nowTicks < nextCleanup || Interlocked.CompareExchange(
                ref _nextCleanupTicks, now.Add(CleanupInterval).UtcDateTime.Ticks, nextCleanup) != nextCleanup)
            return;

        foreach (var pair in _rings)
        {
            if (pair.Value.ExpiresAtTicks <= nowTicks)
                RemoveExact(pair.Key, pair.Value);
        }
    }

    private void RemoveExact(string sessionId, LatencyEntry entry)
        => ((ICollection<KeyValuePair<string, LatencyEntry>>)_rings).Remove(new KeyValuePair<string, LatencyEntry>(sessionId, entry));

    private sealed class LatencyEntry(LatencyRing ring, long expiresAtTicks)
    {
        public LatencyRing Ring { get; } = ring;
        public long ExpiresAtTicks { get; } = expiresAtTicks;
    }

    /// <summary>环形 buffer：容量按需扩容，保留从最旧到最新的时间顺序。</summary>
    private sealed class LatencyRing
    {
        private long[] _buf;
        private int _count;       // 已写入条数（>0）
        private int _writeIdx;    // 下次写入位置
        private readonly object _lock = new();

        public LatencyRing(int windowSize)
        {
            _buf = new long[windowSize];
        }

        public void Push(long v, int windowSize)
        {
            lock (_lock)
            {
                EnsureCapacity(windowSize);

                _buf[_writeIdx] = v;
                _writeIdx = (_writeIdx + 1) % _buf.Length;
                if (_count < _buf.Length) _count++;
            }
        }

        public bool TryGetAverage(out double avg, int minSamples = 1)
        {
            avg = 0;
            if (minSamples <= 0) return false;

            lock (_lock)
            {
                if (_count < minSamples) return false;

                int windowSize = minSamples == 1 ? _count : minSamples;
                long sum = 0;
                int firstIndex = (_writeIdx - windowSize + _buf.Length) % _buf.Length;
                for (int i = 0; i < windowSize; i++)
                    sum += _buf[(firstIndex + i) % _buf.Length];

                avg = (double)sum / windowSize;
                return true;
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buf.Length) return;

            int newCapacity = _buf.Length;
            while (newCapacity < required && newCapacity <= int.MaxValue / 2)
                newCapacity *= 2;
            if (newCapacity < required)
                newCapacity = required;

            var resized = new long[newCapacity];
            int oldestIndex = (_writeIdx - _count + _buf.Length) % _buf.Length;
            for (int i = 0; i < _count; i++)
                resized[i] = _buf[(oldestIndex + i) % _buf.Length];

            _buf = resized;
            _writeIdx = _count;
        }
    }
}
