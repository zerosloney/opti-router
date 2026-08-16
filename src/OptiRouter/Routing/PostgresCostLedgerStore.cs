using Npgsql;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 PostgreSQL 的分布式共享成本账本实现。
/// 支持多节点 K8s 部署环境，借助 Postgres 的 ACID 事务与 ON CONFLICT 行级锁提供高可用计费。
/// </summary>
public sealed class PostgresCostLedgerStore : ICostLedgerStore
{
    private readonly string? _connectionString;
    private readonly ICostLedgerStore _fallback;
    private bool _disposed;

    /// <summary>
    /// 初始化 PostgreSQL 成本账本。
    /// </summary>
    public PostgresCostLedgerStore(string? connectionString, ICostLedgerStore? fallback = null)
    {
        _connectionString = connectionString;
        _fallback = fallback ?? new InMemoryCostLedgerStore();

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                EnsureTablesCreated();
            }
            catch
            {
                // 初始化异常时优雅降级
            }
        }
    }

    private void EnsureTablesCreated()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS optirouter_daily_cost (
    utc_date DATE PRIMARY KEY,
    amount NUMERIC NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS optirouter_session_cost (
    session_id TEXT PRIMARY KEY,
    amount NUMERIC NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS optirouter_total_cost (
    id INT PRIMARY KEY,
    amount NUMERIC NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS optirouter_circuit_state (
    model_name TEXT PRIMARY KEY,
    is_open BOOLEAN NOT NULL DEFAULT FALSE,
    open_until TIMESTAMPTZ NULL
);
";
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.AddDaily(utcDate, delta);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO optirouter_daily_cost (utc_date, amount)
VALUES (@date, @delta)
ON CONFLICT (utc_date) DO UPDATE
SET amount = optirouter_daily_cost.amount + EXCLUDED.amount
RETURNING amount;
";
            cmd.Parameters.AddWithValue("date", utcDate.Date);
            cmd.Parameters.AddWithValue("delta", delta);
            var res = cmd.ExecuteScalar();
            return res is decimal d ? d : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.AddDaily(utcDate, delta);
        }
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.AddTotal(delta);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO optirouter_total_cost (id, amount)
VALUES (1, @delta)
ON CONFLICT (id) DO UPDATE
SET amount = optirouter_total_cost.amount + EXCLUDED.amount
RETURNING amount;
";
            cmd.Parameters.AddWithValue("delta", delta);
            var res = cmd.ExecuteScalar();
            return res is decimal d ? d : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.AddTotal(delta);
        }
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetTotal();

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_total_cost WHERE id = 1;";
            var res = cmd.ExecuteScalar();
            return res is null or DBNull ? 0m : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.GetTotal();
        }
    }

    /// <inheritdoc />
    public void ResetTotal()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ResetTotal(); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_total_cost WHERE id = 1;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _fallback.ResetTotal();
        }
    }

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId)) return _fallback.AddSession(sessionId, delta);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO optirouter_session_cost (session_id, amount, updated_at)
VALUES (@sid, @delta, NOW())
ON CONFLICT (session_id) DO UPDATE
SET amount = optirouter_session_cost.amount + EXCLUDED.amount,
    updated_at = NOW()
RETURNING amount;
";
            cmd.Parameters.AddWithValue("sid", sessionId);
            cmd.Parameters.AddWithValue("delta", delta);
            var res = cmd.ExecuteScalar();
            return res is decimal d ? d : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.AddSession(sessionId, delta);
        }
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetDaily(utcDate);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_daily_cost WHERE utc_date = @date;";
            cmd.Parameters.AddWithValue("date", utcDate.Date);
            var res = cmd.ExecuteScalar();
            return res is null or DBNull ? 0m : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.GetDaily(utcDate);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetDailyHistory(days);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT utc_date, amount FROM optirouter_daily_cost ORDER BY utc_date DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("limit", days);

            var list = new List<(DateTime Date, decimal Amount)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((reader.GetDateTime(0), reader.GetDecimal(1)));
            }
            list.Reverse();
            return list;
        }
        catch
        {
            return _fallback.GetDailyHistory(days);
        }
    }

    /// <inheritdoc />
    public void SnapshotDaily(DateTime utcDate)
    {
    }

    /// <inheritdoc />
    public decimal GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId)) return _fallback.GetSession(sessionId);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT amount FROM optirouter_session_cost WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("sid", sessionId);
            var res = cmd.ExecuteScalar();
            return res is null or DBNull ? 0m : Convert.ToDecimal(res);
        }
        catch
        {
            return _fallback.GetSession(sessionId);
        }
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ResetDaily(); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_daily_cost WHERE utc_date = @date;";
            cmd.Parameters.AddWithValue("date", DateTime.UtcNow.Date);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _fallback.ResetDaily();
        }
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(sessionId)) { _fallback.ResetSession(sessionId); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_session_cost WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("sid", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _fallback.ResetSession(sessionId);
        }
    }

    /// <inheritdoc />
    public int EvictSessionsBefore(DateTime cutoff)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.EvictSessionsBefore(cutoff);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_session_cost WHERE updated_at < @cutoff;";
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
        catch
        {
            return _fallback.EvictSessionsBefore(cutoff);
        }
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.ClearAll(); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "TRUNCATE TABLE optirouter_daily_cost, optirouter_session_cost, optirouter_total_cost, optirouter_circuit_state;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _fallback.ClearAll();
        }
    }



    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO optirouter_circuit_state (model_name, is_open, open_until)
VALUES (@m, @open, @until)
ON CONFLICT (model_name) DO UPDATE
SET is_open = EXCLUDED.is_open, open_until = EXCLUDED.open_until;
";
            cmd.Parameters.AddWithValue("m", modelName);
            cmd.Parameters.AddWithValue("open", state == CircuitState.Open);
            cmd.Parameters.AddWithValue("until", cooldownUntil);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            _fallback.SaveCircuitState(modelName, state, failureCount, cooldownUntil);
        }
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.LoadCircuitStates();

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT model_name, is_open, open_until FROM optirouter_circuit_state;";
            using var reader = cmd.ExecuteReader();
            var dict = new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                string model = reader.GetString(0);
                bool isOpen = reader.GetBoolean(1);
                DateTime cooldown = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2);
                dict[model] = (isOpen ? CircuitState.Open : CircuitState.Closed, isOpen ? 5 : 0, cooldown);
            }
            return dict;
        }
        catch
        {
            return _fallback.LoadCircuitStates();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fallback.Dispose();
    }
}
