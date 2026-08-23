using System.Globalization;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 MariaDB/MySQL 的成本账本存储，表结构与语义对齐 <see cref="SqliteCostLedgerStore"/>
/// （单行当日累计 + 历史归档 + 毫分整数），供多节点部署共享全局预算与断路器状态。
/// </summary>
/// <remarks>
/// 服务器型数据库按操作从连接池取连接（MySqlConnector 默认池化），无需单连接 lock。
/// 故障降级沿用 <see cref="PostgresCostLedgerStore"/> 模式：单次 Error 日志 + 回退内存 store，
/// 恢复后回写 MariaDB，但降级窗口内的花费不回灌。
/// </remarks>
public sealed class MariaDbCostLedgerStore : ICostLedgerStore
{
    private readonly string? _connectionString;
    private readonly ICostLedgerStore _fallback;
    private readonly ILogger? _logger;
    private readonly OptiRouter.Health.AlertHistory? _alertHistory;
    // 0 = MariaDB 正常，1 = 已降级内存。按状态迁移记日志，DB 故障期间不逐请求刷屏。
    private int _degraded;
    private bool _disposed;

    /// <summary>
    /// 用 MariaDB 连接串构造。建表失败时降级内存 store（与 Postgres 实现一致）。
    /// </summary>
    /// <param name="connectionString">MariaDB 连接串（StoreProvider=MariaDb 时必填）。</param>
    /// <param name="fallback">降级回退 store（默认内存账本）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    /// <param name="alertHistory">降级/恢复事件同步记入告警历史（可选；Dashboard/Webhook 可见）。</param>
    public MariaDbCostLedgerStore(string? connectionString, ICostLedgerStore? fallback = null, ILogger? logger = null,
        OptiRouter.Health.AlertHistory? alertHistory = null)
    {
        _connectionString = connectionString;
        _fallback = fallback ?? new InMemoryCostLedgerStore();
        _logger = logger;
        _alertHistory = alertHistory;

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                EnsureTablesCreated();
            }
            catch (Exception ex)
            {
                MarkDegraded(nameof(EnsureTablesCreated), ex);
            }
        }
    }

    private void MarkDegraded(string operation, Exception ex)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            _logger?.LogError(ex,
                "MariaDB cost ledger degraded: {Operation} failed, falling back to in-memory store. " +
                "Budget/circuit state is per-node until MariaDB recovers and is NOT merged back on recovery",
                operation);
            _alertHistory?.Record(OptiRouter.Health.DegradationAlerts.Degraded("cost-ledger-mariadb",
                $"MariaDB cost ledger degraded ({operation} failed); budget/circuit state is per-node until recovery"));
        }
    }

    private void MarkRecovered()
    {
        if (Interlocked.Exchange(ref _degraded, 0) == 1)
        {
            _logger?.LogWarning(
                "MariaDB cost ledger recovered; subsequent writes go to MariaDB again. " +
                "Note: spend recorded during the degraded window stayed in the in-memory fallback and was not merged");
            _alertHistory?.Record(OptiRouter.Health.DegradationAlerts.Recovered("cost-ledger-mariadb",
                "MariaDB cost ledger recovered; degraded-window spend was not merged back"));
        }
    }

    private void EnsureTablesCreated()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        // 与 SQLite 版同构：date/updated_at 存 UTC 字符串（字典序=时间序），amount 为毫分整数。
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS optirouter_daily_spend (
                date VARCHAR(10) NOT NULL PRIMARY KEY,
                amount BIGINT NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS optirouter_session_spend (
                session_id VARCHAR(255) NOT NULL PRIMARY KEY,
                amount BIGINT NOT NULL DEFAULT 0,
                updated_at VARCHAR(32) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS optirouter_total_spend (
                id TINYINT NOT NULL PRIMARY KEY,
                amount BIGINT NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS optirouter_daily_spend_history (
                date VARCHAR(10) NOT NULL PRIMARY KEY,
                amount BIGINT NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS optirouter_model_circuits (
                model_name VARCHAR(255) NOT NULL PRIMARY KEY,
                state VARCHAR(32) NOT NULL,
                failure_count INT NOT NULL,
                cooldown_until VARCHAR(32) NOT NULL
            );
            INSERT IGNORE INTO optirouter_total_spend (id, amount) VALUES (1, 0);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void RecordAtomic(DateTime utcDate, decimal dailyDelta, decimal totalDelta, string? sessionId, decimal? sessionDelta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _fallback.RecordAtomic(utcDate, dailyDelta, totalDelta, sessionId, sessionDelta);
            return;
        }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;

                cmd.CommandText = """
                    INSERT INTO optirouter_daily_spend (date, amount) VALUES (@date, @dailyDelta)
                    ON DUPLICATE KEY UPDATE amount = amount + VALUES(amount);
                    """;
                cmd.Parameters.AddWithValue("@date", FormatDate(utcDate));
                cmd.Parameters.AddWithValue("@dailyDelta", ToMills(dailyDelta));
                cmd.ExecuteNonQuery();

                if (totalDelta != 0m)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = "UPDATE optirouter_total_spend SET amount = amount + @totalDelta WHERE id = 1;";
                    cmd.Parameters.AddWithValue("@totalDelta", ToMills(totalDelta));
                    cmd.ExecuteNonQuery();
                }

                if (!string.IsNullOrEmpty(sessionId) && sessionDelta.HasValue)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = """
                        INSERT INTO optirouter_session_spend (session_id, amount, updated_at) VALUES (@sid, @sessionDelta, @ts)
                        ON DUPLICATE KEY UPDATE amount = amount + VALUES(amount), updated_at = VALUES(updated_at);
                        """;
                    cmd.Parameters.AddWithValue("@sid", sessionId);
                    cmd.Parameters.AddWithValue("@sessionDelta", ToMills(sessionDelta.Value));
                    cmd.Parameters.AddWithValue("@ts", FormatTimestamp(DateTime.UtcNow));
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            // 事务整体失败（三个账户都未写入），降级回退到内存 store 保持与 Postgres 实现一致的降级语义。
            MarkDegraded(nameof(RecordAtomic), ex);
            _fallback.RecordAtomic(utcDate, dailyDelta, totalDelta, sessionId, sessionDelta);
        }
    }

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.AddDaily(utcDate, delta);

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            // MariaDB 无 UPDATE ... RETURNING：先累加再回读（同连接可见自身写入）。
            // 多节点并发时回读值可能包含其它节点的增量，仅影响返回值展示，不影响账本正确性。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO optirouter_daily_spend (date, amount) VALUES (@date, @delta)
                    ON DUPLICATE KEY UPDATE amount = amount + VALUES(amount);
                    """;
                cmd.Parameters.AddWithValue("@date", FormatDate(utcDate));
                cmd.Parameters.AddWithValue("@delta", ToMills(delta));
                cmd.ExecuteNonQuery();
            }
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT amount FROM optirouter_daily_spend WHERE date = @date;";
            read.Parameters.AddWithValue("@date", FormatDate(utcDate));
            MarkRecovered();
            return FromMills(read.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(AddDaily), ex);
            return _fallback.AddDaily(utcDate, delta);
        }
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.AddTotal(delta);
        if (delta == 0m) return GetTotal();

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE optirouter_total_spend SET amount = amount + @delta WHERE id = 1;";
                cmd.Parameters.AddWithValue("@delta", ToMills(delta));
                cmd.ExecuteNonQuery();
            }
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT amount FROM optirouter_total_spend WHERE id = 1;";
            MarkRecovered();
            return FromMills(read.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(AddTotal), ex);
            return _fallback.AddTotal(delta);
        }
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetTotal();

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_total_spend WHERE id = 1;";
            MarkRecovered();
            return FromMills(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(GetTotal), ex);
            return _fallback.GetTotal();
        }
    }

    /// <inheritdoc />
    public void ResetTotal()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ResetTotal(); return; }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE optirouter_total_spend SET amount = 0 WHERE id = 1;";
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(ResetTotal), ex);
            _fallback.ResetTotal();
        }
    }

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId))
            return _fallback.AddSession(sessionId, delta);

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO optirouter_session_spend (session_id, amount, updated_at) VALUES (@sid, @delta, @ts)
                    ON DUPLICATE KEY UPDATE amount = amount + VALUES(amount), updated_at = VALUES(updated_at);
                    """;
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@delta", ToMills(delta));
                cmd.Parameters.AddWithValue("@ts", FormatTimestamp(DateTime.UtcNow));
                cmd.ExecuteNonQuery();
            }
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT amount FROM optirouter_session_spend WHERE session_id = @sid;";
            read.Parameters.AddWithValue("@sid", sessionId);
            MarkRecovered();
            return FromMills(read.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(AddSession), ex);
            return _fallback.AddSession(sessionId, delta);
        }
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetDaily(utcDate);

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_daily_spend WHERE date = @date;";
            cmd.Parameters.AddWithValue("@date", FormatDate(utcDate));
            MarkRecovered();
            return FromMills(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(GetDaily), ex);
            return _fallback.GetDaily(utcDate);
        }
    }

    /// <inheritdoc />
    public decimal GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId))
            return _fallback.GetSession(sessionId);

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_session_spend WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            MarkRecovered();
            return FromMills(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(GetSession), ex);
            return _fallback.GetSession(sessionId);
        }
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ResetDaily(); return; }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 仅清空当日累计（单行表结构，与 SQLite 版语义一致）；归档由 CostLedger 先行 SnapshotDaily 完成。
            cmd.CommandText = "DELETE FROM optirouter_daily_spend;";
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(ResetDaily), ex);
            _fallback.ResetDaily();
        }
    }

    /// <inheritdoc />
    public void SnapshotDaily(DateTime utcDate)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.SnapshotDaily(utcDate); return; }

        try
        {
            string todayKey = FormatDate(utcDate);
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            // Only snapshot if there's actual spend for today.
            long mills = ReadDailyMills(conn, todayKey);
            if (mills == 0) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO optirouter_daily_spend_history (date, amount) VALUES (@date, @amount)
                ON DUPLICATE KEY UPDATE amount = VALUES(amount);
                """;
            cmd.Parameters.AddWithValue("@date", todayKey);
            cmd.Parameters.AddWithValue("@amount", mills);
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(SnapshotDaily), ex);
            _fallback.SnapshotDaily(utcDate);
        }
    }

    private static long ReadDailyMills(MySqlConnection conn, string dateKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT amount FROM optirouter_daily_spend WHERE date = @date;";
        cmd.Parameters.AddWithValue("@date", dateKey);
        var result = cmd.ExecuteScalar();
        return result is null || result == DBNull.Value ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || days <= 0)
            return days <= 0 ? Array.Empty<(DateTime, decimal)>() : _fallback.GetDailyHistory(days);

        try
        {
            var result = new List<(DateTime, decimal)>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT date, amount FROM optirouter_daily_spend_history
                WHERE date >= @cutoff
                ORDER BY date ASC;
                """;
            cmd.Parameters.AddWithValue("@cutoff", FormatDate(DateTime.UtcNow.AddDays(-days)));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string dateStr = reader.GetString(0);
                if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    result.Add((date, FromMills(reader.GetValue(1))));
                }
            }
            MarkRecovered();
            return result;
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(GetDailyHistory), ex);
            return _fallback.GetDailyHistory(days);
        }
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId))
        {
            _fallback.ResetSession(sessionId);
            return;
        }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_session_spend WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(ResetSession), ex);
            _fallback.ResetSession(sessionId);
        }
    }

    /// <inheritdoc />
    public int EvictSessionsBefore(DateTime cutoff)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.EvictSessionsBefore(cutoff);

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_session_spend WHERE updated_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", FormatTimestamp(cutoff));
            int removed = cmd.ExecuteNonQuery();
            MarkRecovered();
            return removed;
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(EvictSessionsBefore), ex);
            return _fallback.EvictSessionsBefore(cutoff);
        }
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ClearAll(); return; }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 与 SQLite 版 ClearAll 对齐：清日/会话累计并归零总额，历史归档与断路器状态保留。
            cmd.CommandText = """
                DELETE FROM optirouter_daily_spend;
                DELETE FROM optirouter_session_spend;
                UPDATE optirouter_total_spend SET amount = 0 WHERE id = 1;
                """;
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(ClearAll), ex);
            _fallback.ClearAll();
        }
    }

    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(modelName))
        {
            _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil);
            return;
        }

        try
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO optirouter_model_circuits (model_name, state, failure_count, cooldown_until)
                VALUES (@model, @state, @failures, @cooldown)
                ON DUPLICATE KEY UPDATE
                    state = VALUES(state),
                    failure_count = VALUES(failure_count),
                    cooldown_until = VALUES(cooldown_until);
                """;
            cmd.Parameters.AddWithValue("@model", modelName);
            cmd.Parameters.AddWithValue("@state", state.ToString());
            cmd.Parameters.AddWithValue("@failures", failureCount);
            cmd.Parameters.AddWithValue("@cooldown", FormatTimestamp(cooldownUntil));
            cmd.ExecuteNonQuery();
            MarkRecovered();
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(SaveCircuitState), ex);
            _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil);
        }
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.LoadCircuitStates();

        try
        {
            var result = new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(StringComparer.Ordinal);
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT model_name, state, failure_count, cooldown_until FROM optirouter_model_circuits;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string model = reader.GetString(0);
                string stateStr = reader.GetString(1);
                int failures = reader.GetInt32(2);
                string cooldownStr = reader.GetString(3);

                if (Enum.TryParse<CircuitState>(stateStr, out var state))
                {
                    DateTime cooldown = DateTime.TryParse(cooldownStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var cd)
                        ? cd
                        : default;

                    result[model] = (state, failures, cooldown);
                }
            }
            MarkRecovered();
            return result;
        }
        catch (Exception ex)
        {
            MarkDegraded(nameof(LoadCircuitStates), ex);
            return _fallback.LoadCircuitStates();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fallback.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>将 decimal（美元）转为 long（毫分），四舍五入避免截断损失。</summary>
    private static long ToMills(decimal usd) => (long)Math.Round(usd * 1000m, MidpointRounding.ToEven);

    /// <summary>将 long（毫分）转为 decimal（美元）。</summary>
    private static decimal FromMills(object? value)
    {
        if (value is null || value == DBNull.Value) return 0m;
        return (decimal)Convert.ToInt64(value, CultureInfo.InvariantCulture) / 1000m;
    }

    private static string FormatDate(DateTime utc)
        => utc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
