using System.Collections.Concurrent;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 内存实现的请求审计存储，线程安全。环形缓冲区限制最大条数，防止内存泄漏。
/// </summary>
public sealed class InMemoryRequestAuditStore : IRequestAuditStore, IDisposable
{
    private const int DefaultCapacity = 10000;

    private readonly object _lock = new();
    private readonly RequestAuditRecord[] _buffer;
    private int _head;
    private int _count;
    private bool _disposed;

    /// <summary>
    /// 用默认容量（10000）构造。
    /// </summary>
    public InMemoryRequestAuditStore() : this(DefaultCapacity) { }

    /// <summary>
    /// 用指定容量构造。
    /// </summary>
    /// <param name="capacity">最大保留条数。</param>
    public InMemoryRequestAuditStore(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new RequestAuditRecord[capacity];
    }

    /// <inheritdoc />
    public void Append(RequestAuditRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            int idx = (_head + _count) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _buffer[idx] = record;
                _count++;
            }
            else
            {
                // 环形缓冲满了：覆盖最旧的（_head 位置），head 前移。
                _buffer[_head] = record;
                _head = (_head + 1) % _buffer.Length;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RequestAuditRecord> GetRecent(int limit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (limit <= 0) return Array.Empty<RequestAuditRecord>();

        lock (_lock)
        {
            int count = Math.Min(limit, _count);
            var result = new List<RequestAuditRecord>(count);
            // 从最新到最旧遍历。
            for (int i = 0; i < count; i++)
            {
                int idx = (_head + _count - 1 - i) % _buffer.Length;
                result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RequestAuditRecord> GetByModel(string modelName, int limit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(modelName) || limit <= 0)
            return Array.Empty<RequestAuditRecord>();

        lock (_lock)
        {
            var result = new List<RequestAuditRecord>();
            // 从最新到最旧遍历，收集匹配模型。
            for (int i = 0; i < _count && result.Count < limit; i++)
            {
                int idx = (_head + _count - 1 - i) % _buffer.Length;
                if (string.Equals(_buffer[idx].Model, modelName, StringComparison.Ordinal))
                    result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    /// <inheritdoc />
    public (IReadOnlyList<RequestAuditRecord> Items, int TotalCount) GetByTimeRange(DateTime from, DateTime to, int limit, int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (limit <= 0) return (Array.Empty<RequestAuditRecord>(), 0);
        if (offset < 0) offset = 0;

        List<RequestAuditRecord> allInRange;

        lock (_lock)
        {
            allInRange = new List<RequestAuditRecord>(_count);
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _buffer.Length;
                var r = _buffer[idx];
                if (r.Timestamp >= from && r.Timestamp <= to)
                    allInRange.Add(r);
            }
        }

        // 按时间倒序排列。
        allInRange.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

        int totalCount = allInRange.Count;
        int skip = Math.Min(offset, totalCount);
        var page = allInRange.Skip(skip).Take(limit).ToList();
        return (page, totalCount);
    }

    /// <inheritdoc />
    public (int Failures, int Total) GetFailureStats(DateTime from, DateTime to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int failures = 0, total = 0;
        lock (_lock)
        {
            // 单次遍历计数，O(n)，与 GetLatencyStatsSince 同模式。替代全量物化。
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _buffer.Length;
                var r = _buffer[idx];
                if (r.Timestamp >= from && r.Timestamp <= to)
                {
                    total++;
                    if (!r.Success) failures++;
                }
            }
        }
        return (failures, total);
    }

    /// <inheritdoc />
    public int EvictBefore(DateTime cutoff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_count == 0) return 0;

            // 扫描并重排：保留 cutoff 之后的记录，顺序不变。
            var kept = new List<RequestAuditRecord>(_count);
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _buffer.Length;
                if (_buffer[idx].Timestamp >= cutoff)
                    kept.Add(_buffer[idx]);
            }

            int evicted = _count - kept.Count;
            if (evicted == 0) return 0;

            // 重写 buffer。
            _count = kept.Count;
            for (int i = 0; i < _count; i++)
                _buffer[i] = kept[i];
            _head = 0;

            return evicted;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, (double AverageLatencyMs, int SampleCount)> GetLatencyStatsSince(DateTime since)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // intentional-simple: 单次遍历累加，O(n)。审计缓冲通常 ≤10K 条，后台聚合低频，无需预聚合索引。
        var sumMs = new Dictionary<string, double>(StringComparer.Ordinal);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);

        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _buffer.Length;
                var r = _buffer[idx];
                if (r.Timestamp < since || !r.Success)
                    continue;

                sumMs.TryGetValue(r.Model, out double sum);
                sumMs[r.Model] = sum + r.LatencyMs;
                count.TryGetValue(r.Model, out int c);
                count[r.Model] = c + 1;
            }
        }

        var result = new Dictionary<string, (double, int)>(sumMs.Count, StringComparer.Ordinal);
        foreach (var model in sumMs.Keys)
        {
            int n = count[model];
            result[model] = (sumMs[model] / n, n);
        }
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
