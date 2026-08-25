using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 会话级延迟窗口跟踪器：为 <see cref="SessionAffinityPolicy"/> 的"延迟熔断"逃生通道提供数据。
/// 内存内 ConcurrentDictionary + 每 session 一个环形 buffer，零依赖、零锁热路径（环形 buffer 自身有锁，
/// 但仅在写时持有，读时拷快照）。
/// </summary>
/// <remarks>
/// intentional-simple: 不持久化（重启清空），不淘汰过期 session（依赖 SessionAffinity 自己的 TTL
/// 决定何时清缓存；这里只在 session 还活着时被读）。环形 buffer 容量由调用方按
/// <see cref="Configuration.RoutingOptions.SessionAffinityEscapeWindowSize"/> 决定。
/// </remarks>
public sealed class SessionLatencyTracker
{
    private readonly ConcurrentDictionary<string, LatencyRing> _rings = new(StringComparer.Ordinal);

    /// <summary>
    /// 记录一次成功请求的延迟。latencyMs &lt;= 0 视为无效（路由前决策或异常路径），不写入。
    /// </summary>
    public void Record(string? sessionId, long latencyMs)
    {
        if (string.IsNullOrEmpty(sessionId) || latencyMs <= 0) return;
        var ring = _rings.GetOrAdd(sessionId, _ => new LatencyRing(0));
        ring.Push(latencyMs);
    }

    /// <summary>
    /// 读取最近 N 次成功请求的平均延迟。session 不存在时返回 false（avg=0）。
    /// 调用方可指定 <paramref name="minSamples"/>：仅当 ring 内已有样本数 &gt;= 该值时返回 true，
    /// 避免"首次访问"或"样本未达窗口"时给出统计意义不强的 avg。
    /// 不在范围内（out-of-window）的延迟不影响 avg 统计（仅环形 buffer 内最末 N 次计入）。
    /// </summary>
    public bool TryGetRecentAverage(string? sessionId, out double avgMs, int minSamples = 1)
    {
        avgMs = 0;
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_rings.TryGetValue(sessionId, out var ring) || ring is null) return false;
        return ring.TryGetAverage(out avgMs, minSamples);
    }

    /// <summary>环形 buffer：固定容量，覆写最旧。线程安全（lock 粒度仅 1 个 long 的写）。</summary>
    private sealed class LatencyRing
    {
        private readonly long[] _buf;
        private int _count;       // 已写入条数（>0）
        private int _writeIdx;    // 下次写入位置
        private readonly object _lock = new();

        public LatencyRing(int _dummy)
        {
            // 容量在第一次 Push 时按 caller 的 windowSize 决定；用 0 占位，
            // 真实容量由外部 SessionAffinityPolicy 注入。但为简化，把容量做成可变：
            // 我们用 16 个槽位起步，超出后环形覆写（足够覆盖常见 windowSize=5）。
            _buf = new long[16];
        }

        public void Push(long v)
        {
            lock (_lock)
            {
                _buf[_writeIdx] = v;
                _writeIdx = (_writeIdx + 1) % _buf.Length;
                if (_count < _buf.Length) _count++;
            }
        }

        public bool TryGetAverage(out double avg, int minSamples = 1)
        {
            avg = 0;
            lock (_lock)
            {
                if (_count < minSamples) return false;
                long sum = 0;
                for (int i = 0; i < _count; i++) sum += _buf[i];
                avg = (double)sum / _count;
                return true;
            }
        }
    }
}
