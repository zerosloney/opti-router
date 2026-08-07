namespace OptiRouter.Routing;

/// <summary>
/// 内存实现的成本账本存储，线程安全。
/// 默认实现：单元测试、测试 host、<c>UsePersistentStore=false</c> 场景。
/// 进程重启即丢失全部状态。
/// </summary>
public sealed class InMemoryCostLedgerStore : ICostLedgerStore
{
    private readonly object _lock = new();
    private readonly Dictionary<DateTime, decimal> _daily = new();
    private readonly Dictionary<string, (decimal Amount, DateTime LastSeen)> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> _circuits = new(StringComparer.Ordinal);
    private decimal _total;
    private bool _disposed;

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta == 0m) return GetDaily(utcDate);

        DateTime key = utcDate.Date;
        lock (_lock)
        {
            _daily.TryGetValue(key, out decimal current);
            current += delta;
            _daily[key] = current;
            return current;
        }
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta == 0m) return _total;
        lock (_lock)
        {
            _total += delta;
            return _total;
        }
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            return _total;
        }
    }

    /// <inheritdoc />
    public void ResetTotal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _total = 0m;
        }
    }

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out var entry);
            decimal newAmount = entry.Amount + delta;
            _sessions[sessionId] = (newAmount, now);
            return newAmount;
        }
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            return _daily.TryGetValue(utcDate.Date, out decimal v) ? v : 0m;
        }
    }

    /// <inheritdoc />
    public decimal GetSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Amount : 0m;
        }
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _daily.Clear();
        }
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_lock)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <inheritdoc />
    public int EvictSessionsBefore(DateTime cutoff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTime threshold = cutoff.ToUniversalTime();
        lock (_lock)
        {
            var stale = _sessions.Where(kv => kv.Value.LastSeen < threshold).Select(kv => kv.Key).ToList();
            foreach (var sid in stale)
                _sessions.Remove(sid);
            return stale.Count;
        }
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            _daily.Clear();
            _sessions.Clear();
            _total = 0m;
        }
    }

    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modelName);

        lock (_lock)
        {
            _circuits[modelName] = (state, failureCount, cooldownUntil);
        }
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            return new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(_circuits, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _daily.Clear();
            _sessions.Clear();
            _total = 0m;
        }
        GC.SuppressFinalize(this);
    }
}
