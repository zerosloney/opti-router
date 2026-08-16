using StackExchange.Redis;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 StackExchange.Redis 的分布式共享成本账本实现。
/// 适用于 Kubernetes 多节点 Pod 无状态部署，利用 Redis 原子命令 (INCRBYFLOAT, HSET) 保证跨节点并发计费的精准性。
/// </summary>
public sealed class RedisCostLedgerStore : ICostLedgerStore
{
    private readonly ConnectionMultiplexer? _redis;
    private readonly IDatabase? _db;
    private readonly string _prefix;
    private readonly ICostLedgerStore _fallback;
    private bool _disposed;

    /// <summary>
    /// 初始化 Redis 成本账本。
    /// </summary>
    public RedisCostLedgerStore(string? connectionString, string prefix = "optirouter:", ICostLedgerStore? fallback = null)
    {
        _prefix = prefix ?? "optirouter:";
        _fallback = fallback ?? new InMemoryCostLedgerStore();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _redis = null;
            _db = null;
            return;
        }

        try
        {
            _redis = ConnectionMultiplexer.Connect(connectionString);
            _db = _redis.GetDatabase();
        }
        catch
        {
            _redis = null;
            _db = null;
        }
    }

    /// <summary>
    /// 使用既有 ConnectionMultiplexer 初始化。
    /// </summary>
    public RedisCostLedgerStore(ConnectionMultiplexer redis, string prefix = "optirouter:", ICostLedgerStore? fallback = null)
    {
        _prefix = prefix ?? "optirouter:";
        _fallback = fallback ?? new InMemoryCostLedgerStore();
        _redis = redis;
        _db = redis.GetDatabase();
    }

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        if (_db is null) return _fallback.AddDaily(utcDate, delta);

        string key = $"{_prefix}daily:{utcDate:yyyy-MM-dd}";
        double result = _db.StringIncrement(key, (double)delta);
        return (decimal)result;
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        if (_db is null) return _fallback.AddTotal(delta);

        string key = $"{_prefix}total";
        double result = _db.StringIncrement(key, (double)delta);
        return (decimal)result;
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        if (_db is null) return _fallback.GetTotal();

        string key = $"{_prefix}total";
        var val = _db.StringGet(key);
        return val.HasValue && double.TryParse(val, out double d) ? (decimal)d : 0m;
    }

    /// <inheritdoc />
    public void ResetTotal()
    {
        if (_db is null) { _fallback.ResetTotal(); return; }
        _db.KeyDelete($"{_prefix}total");
    }

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        if (_db is null || string.IsNullOrWhiteSpace(sessionId)) return _fallback.AddSession(sessionId, delta);

        string key = $"{_prefix}session:{sessionId}";
        double result = _db.StringIncrement(key, (double)delta);
        _db.KeyExpire(key, TimeSpan.FromDays(1));
        return (decimal)result;
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        if (_db is null) return _fallback.GetDaily(utcDate);

        string key = $"{_prefix}daily:{utcDate:yyyy-MM-dd}";
        var val = _db.StringGet(key);
        return val.HasValue && double.TryParse(val, out double d) ? (decimal)d : 0m;
    }

    /// <inheritdoc />
    public IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days)
    {
        if (_db is null) return _fallback.GetDailyHistory(days);

        var list = new List<(DateTime Date, decimal Amount)>();
        var today = DateTime.UtcNow.Date;
        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            decimal amt = GetDaily(date);
            list.Add((date, amt));
        }
        return list;
    }

    /// <inheritdoc />
    public void SnapshotDaily(DateTime utcDate)
    {
        // Redis 节点数据天生持久共享，已通过键区分日期，无需额外动作
    }

    /// <inheritdoc />
    public decimal GetSession(string sessionId)
    {
        if (_db is null || string.IsNullOrWhiteSpace(sessionId)) return _fallback.GetSession(sessionId);

        string key = $"{_prefix}session:{sessionId}";
        var val = _db.StringGet(key);
        return val.HasValue && double.TryParse(val, out double d) ? (decimal)d : 0m;
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        if (_db is null) { _fallback.ResetDaily(); return; }
        string key = $"{_prefix}daily:{DateTime.UtcNow.Date:yyyy-MM-dd}";
        _db.KeyDelete(key);
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        if (_db is null || string.IsNullOrWhiteSpace(sessionId)) { _fallback.ResetSession(sessionId); return; }
        _db.KeyDelete($"{_prefix}session:{sessionId}");
    }

    /// <inheritdoc />
    public int EvictSessionsBefore(DateTime cutoff)
    {
        // Session 键自动设置 TTL (24小时)，依赖 Redis 引擎自动回收
        return 0;
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        if (_db is null) { _fallback.ClearAll(); return; }
        var server = _redis?.GetServer(_redis.GetEndPoints().First());
        if (server is null) return;

        foreach (var key in server.Keys(pattern: $"{_prefix}*"))
        {
            _db.KeyDelete(key);
        }
    }



    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        if (_db is null) { _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil); return; }

        string key = $"{_prefix}circuit:{modelName}";
        _db.HashSet(key, new HashEntry[]
        {
            new("state", (int)state),
            new("failureCount", failureCount),
            new("cooldownUntil", cooldownUntil.ToString("O"))
        });
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        if (_db is null || _redis is null) return _fallback.LoadCircuitStates();

        var dict = new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(StringComparer.OrdinalIgnoreCase);
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        if (server is null) return dict;

        foreach (var key in server.Keys(pattern: $"{_prefix}circuit:*"))
        {
            string keyStr = key.ToString();
            string modelName = keyStr.Substring($"{_prefix}circuit:".Length);
            var entries = _db.HashGetAll(key);
            int stateInt = 0, failCount = 0;
            DateTime cooldown = DateTime.MinValue;

            foreach (var entry in entries)
            {
                if (entry.Name == "state") stateInt = (int)entry.Value;
                if (entry.Name == "failureCount") failCount = (int)entry.Value;
                if (entry.Name == "cooldownUntil" && !entry.Value.IsNullOrEmpty)
                {
                    if (DateTime.TryParse(entry.Value, out var dt)) cooldown = dt;
                }
            }

            dict[modelName] = ((CircuitState)stateInt, failCount, cooldown);
        }

        return dict;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _redis?.Dispose();
        _fallback.Dispose();
    }
}
