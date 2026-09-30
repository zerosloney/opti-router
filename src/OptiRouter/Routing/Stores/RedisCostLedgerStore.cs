using StackExchange.Redis;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 StackExchange.Redis 的分布式共享成本账本实现。
/// 适用于 Kubernetes 多节点 Pod 无状态部署，利用 Redis 原子命令 (INCRBYFLOAT, HSET) 保证跨节点并发计费的精准性。
/// <para>
/// 可用性语义：构造连不上与运行期连接故障（<see cref="RedisException"/>）均永久降级内存账本并记错误日志——
/// 不降级则每次计费抛异常被上层吞掉，账本静默停摆直到进程重启。降级后预算/断路器状态退化为单节点。
/// </para>
/// </summary>
public sealed class RedisCostLedgerStore : ICostLedgerStore
{
    private readonly ConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private readonly string _prefix;
    private readonly ICostLedgerStore _fallback;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly OptiRouter.Health.AlertHistory? _alertHistory;
    private bool _disposed;

    /// <summary>
    /// 初始化 Redis 成本账本。
    /// </summary>
    public RedisCostLedgerStore(string? connectionString, string prefix = "optirouter:", ICostLedgerStore? fallback = null,
        Microsoft.Extensions.Logging.ILogger? logger = null, OptiRouter.Health.AlertHistory? alertHistory = null)
    {
        _prefix = prefix ?? "optirouter:";
        _fallback = fallback ?? new InMemoryCostLedgerStore();
        _logger = logger;
        _alertHistory = alertHistory;

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
        catch (Exception ex)
        {
            // 构造失败即永久降级内存（本实例无重连路径）：必须留日志，否则多节点部署下
            // 预算/断路器状态静默退化为单节点，重启归零且无从排查。
            _logger?.LogError(ex,
                "Redis cost ledger unavailable: connection failed, permanently falling back to in-memory store. " +
                "Budget/circuit state will be per-node and lost on restart until the process is restarted with a reachable Redis");
            _alertHistory?.Record(OptiRouter.Health.DegradationAlerts.Degraded("cost-ledger-redis",
                "Redis cost ledger unreachable at startup; permanently degraded to in-memory (per-node state, lost on restart)"));
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

    /// <summary>
    /// 运行期 Redis 故障时永久降级内存账本（与构造失败降级同一语义）。
    /// </summary>
    private void DegradeToMemory(RedisException ex)
    {
        _db = null;
        _logger?.LogError(ex,
            "Redis cost ledger failed at runtime: permanently falling back to in-memory store. " +
            "Budget/circuit state will be per-node and lost on restart until the process is restarted with a reachable Redis");
        _alertHistory?.Record(OptiRouter.Health.DegradationAlerts.Degraded("cost-ledger-redis",
            "Redis cost ledger failed at runtime; permanently degraded to in-memory (per-node state, lost on restart)"));
    }

    /// <summary>带运行期降级守卫的 Redis 读/写：故障时转交 fallback 执行本次操作，不向上抛。</summary>
    private T WithRedis<T>(Func<IDatabase, T> action, Func<T> fallback)
    {
        var db = _db;
        if (db is null) return fallback();
        try
        {
            return action(db);
        }
        catch (RedisException ex)
        {
            DegradeToMemory(ex);
            return fallback();
        }
    }

    /// <summary>带运行期降级守卫的 Redis 操作（void 形态）。</summary>
    private void WithRedis(Action<IDatabase> action, Action fallback)
    {
        var db = _db;
        if (db is null) { fallback(); return; }
        try
        {
            action(db);
        }
        catch (RedisException ex)
        {
            DegradeToMemory(ex);
            fallback();
        }
    }

    /// <inheritdoc />
    public void RecordAtomic(DateTime utcDate, decimal dailyDelta, decimal totalDelta, string? sessionId, decimal? sessionDelta)
    {
        var db = _db;
        if (db is null)
        {
            _fallback.RecordAtomic(utcDate, dailyDelta, totalDelta, sessionId, sessionDelta);
            return;
        }

        try
        {
            // MULTI/EXEC 单事务写入日/总/会话账户，避免部分失败导致账户漂移。
            var tx = db.CreateTransaction();
            _ = tx.StringIncrementAsync($"{_prefix}daily:{utcDate:yyyy-MM-dd}", ToMills(dailyDelta));
            if (totalDelta != 0m)
                _ = tx.StringIncrementAsync($"{_prefix}total", ToMills(totalDelta));
            if (!string.IsNullOrEmpty(sessionId) && sessionDelta.HasValue)
            {
                string sessionKey = $"{_prefix}session:{sessionId}";
                _ = tx.StringIncrementAsync(sessionKey, ToMills(sessionDelta.Value));
                _ = tx.KeyExpireAsync(sessionKey, TimeSpan.FromDays(1));
            }

            if (tx.Execute())
                return;

            // 事务整体未执行（如连接级失败），按接口默认语义顺序写入。
            // AddXxx 各自带运行期降级守卫，僵尸连接下转为内存写入而非抛异常。
            AddDaily(utcDate, dailyDelta);
            AddTotal(totalDelta);
            if (!string.IsNullOrEmpty(sessionId) && sessionDelta.HasValue)
                AddSession(sessionId, sessionDelta.Value);
        }
        catch (RedisException ex)
        {
            DegradeToMemory(ex);
            _fallback.RecordAtomic(utcDate, dailyDelta, totalDelta, sessionId, sessionDelta);
        }
    }

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        string key = $"{_prefix}daily:{utcDate:yyyy-MM-dd}";
        // 用整数毫分（1/1000 USD）避免 INCRBYFLOAT 的 double 精度漂移。
        return WithRedis(
            db => (decimal)db.StringIncrement(key, ToMills(delta)) / 1000m,
            () => _fallback.AddDaily(utcDate, delta));
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        string key = $"{_prefix}total";
        return WithRedis(
            db => (decimal)db.StringIncrement(key, ToMills(delta)) / 1000m,
            () => _fallback.AddTotal(delta));
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        string key = $"{_prefix}total";
        return WithRedis(
            db => ParseMills(db.StringGet(key)),
            () => _fallback.GetTotal());
    }

    /// <inheritdoc />
    public void ResetTotal() =>
        WithRedis(
            db => db.KeyDelete($"{_prefix}total"),
            () => _fallback.ResetTotal());

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return _fallback.AddSession(sessionId, delta);

        string key = $"{_prefix}session:{sessionId}";
        return WithRedis(
            db =>
            {
                long result = db.StringIncrement(key, ToMills(delta));
                db.KeyExpire(key, TimeSpan.FromDays(1));
                return (decimal)result / 1000m;
            },
            () => _fallback.AddSession(sessionId, delta));
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        string key = $"{_prefix}daily:{utcDate:yyyy-MM-dd}";
        return WithRedis(
            db => ParseMills(db.StringGet(key)),
            () => _fallback.GetDaily(utcDate));
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
        if (string.IsNullOrWhiteSpace(sessionId)) return _fallback.GetSession(sessionId);

        string key = $"{_prefix}session:{sessionId}";
        return WithRedis(
            db => ParseMills(db.StringGet(key)),
            () => _fallback.GetSession(sessionId));
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        // Redis stores each UTC date under its own key. The key is also the
        // historical record, so deleting today's key on one node would erase
        // spend already recorded by other nodes. A connected Redis store needs
        // no reset; date-key rotation is handled by AddDaily/GetDaily.
        if (_db is null) _fallback.ResetDaily();
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) { _fallback.ResetSession(sessionId); return; }
        WithRedis(
            db => db.KeyDelete($"{_prefix}session:{sessionId}"),
            () => _fallback.ResetSession(sessionId));
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
        var db = _db;
        if (db is null) { _fallback.ClearAll(); return; }
        try
        {
            var server = _redis?.GetServer(_redis.GetEndPoints().First());
            if (server is null) return;

            foreach (var key in server.Keys(pattern: $"{_prefix}*"))
            {
                db.KeyDelete(key);
            }
        }
        catch (RedisException ex)
        {
            DegradeToMemory(ex);
            _fallback.ClearAll();
        }
    }

    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        string key = $"{_prefix}circuit:{modelName}";
        WithRedis(
            db => db.HashSet(key, new HashEntry[]
            {
                new("state", (int)state),
                new("failureCount", failureCount),
                new("cooldownUntil", cooldownUntil.ToString("O"))
            }),
            () => _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil));
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        var db = _db;
        var redis = _redis;
        if (db is null || redis is null) return _fallback.LoadCircuitStates();

        try
        {
            var dict = new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(StringComparer.OrdinalIgnoreCase);
            var server = redis.GetServer(redis.GetEndPoints().First());
            if (server is null) return dict;

            foreach (var key in server.Keys(pattern: $"{_prefix}circuit:*"))
            {
                string keyStr = key.ToString();
                string modelName = keyStr.Substring($"{_prefix}circuit:".Length);
                var entries = db.HashGetAll(key);
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
        catch (RedisException ex)
        {
            DegradeToMemory(ex);
            return _fallback.LoadCircuitStates();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _redis?.Dispose();
        _fallback.Dispose();
    }

    /// <summary>Redis 整数毫分值 → 美元；缺失/不可解析返回 0。</summary>
    private static decimal ParseMills(RedisValue val)
        => val.HasValue && long.TryParse(val.ToString(), out long mills) ? (decimal)mills / 1000m : 0m;

    /// <summary>将 decimal（美元）转为 long（毫分），四舍五入避免截断损失。</summary>
    private static long ToMills(decimal usd) => (long)Math.Round(usd * 1000m, MidpointRounding.ToEven);
}
