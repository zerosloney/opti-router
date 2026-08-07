using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OptiRouter.Routing;

/// <summary>
/// SQLite 持久化的成本账本存储，线程安全。
/// 单文件 DB（默认 <c>data/optirouter-budget.db</c>），WAL 模式提升并发读。
/// 跨进程重启保留花费状态，使日/会话预算真正生效。
/// </summary>
/// <remarks>
/// 线程安全模型：单 <see cref="SqliteConnection"/>（SQLite 单写者语义）+ 显式 lock 串行化写。
/// 写入低频（每请求最多两次累加），lock 无瓶颈。
/// </remarks>
public sealed class SqliteCostLedgerStore : ICostLedgerStore
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>
    /// 用指定 DB 文件路径构造。文件所在目录需已存在（由调用方 Program.cs 创建）。
    /// </summary>
    /// <param name="path">SQLite 文件绝对或相对路径。</param>
    public SqliteCostLedgerStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        // WAL 模式：提升并发读，崩溃恢复。
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");

        Execute("""
            CREATE TABLE IF NOT EXISTS daily_spend (
                date TEXT PRIMARY KEY,
                amount REAL NOT NULL DEFAULT 0
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS session_spend (
                session_id TEXT PRIMARY KEY,
                amount REAL NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS total_spend (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                amount REAL NOT NULL DEFAULT 0
            );
            """);
        // 确保总累计行存在（单行表，id 固定为 1）。
        Execute("INSERT OR IGNORE INTO total_spend (id, amount) VALUES (1, 0);");

        Execute("""
            CREATE TABLE IF NOT EXISTS model_circuits (
                model_name TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                failure_count INTEGER NOT NULL,
                cooldown_until TEXT NOT NULL
            );
            """);
    }

    /// <inheritdoc />
    public decimal AddDaily(DateTime utcDate, decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string key = FormatDate(utcDate);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO daily_spend (date, amount) VALUES (@date, @delta)
                ON CONFLICT(date) DO UPDATE SET amount = amount + @delta
                RETURNING amount;
                """;
            cmd.Parameters.AddWithValue("@date", key);
            cmd.Parameters.AddWithValue("@delta", delta);

            object? result = cmd.ExecuteScalar();
            tx.Commit();
            return ToDecimal(result);
        }
    }

    /// <inheritdoc />
    public decimal AddTotal(decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (delta == 0m) return GetTotal();

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE total_spend SET amount = amount + @delta WHERE id = 1
                RETURNING amount;
                """;
            cmd.Parameters.AddWithValue("@delta", delta);

            object? result = cmd.ExecuteScalar();
            tx.Commit();
            return ToDecimal(result);
        }
    }

    /// <inheritdoc />
    public decimal GetTotal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT amount FROM total_spend WHERE id = 1;";
            return ToDecimal(cmd.ExecuteScalar());
        }
    }

    /// <inheritdoc />
    public void ResetTotal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            Execute("UPDATE total_spend SET amount = 0 WHERE id = 1;");
        }
    }

    /// <inheritdoc />
    public decimal AddSession(string sessionId, decimal delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO session_spend (session_id, amount, updated_at) VALUES (@sid, @delta, @ts)
                ON CONFLICT(session_id) DO UPDATE SET amount = amount + @delta, updated_at = @ts
                RETURNING amount;
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@delta", delta);
            cmd.Parameters.AddWithValue("@ts", FormatTimestamp(DateTime.UtcNow));

            object? result = cmd.ExecuteScalar();
            tx.Commit();
            return ToDecimal(result);
        }
    }

    /// <inheritdoc />
    public decimal GetDaily(DateTime utcDate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string key = FormatDate(utcDate);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT amount FROM daily_spend WHERE date = @date;";
            cmd.Parameters.AddWithValue("@date", key);
            return ToDecimal(cmd.ExecuteScalar());
        }
    }

    /// <inheritdoc />
    public decimal GetSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT amount FROM session_spend WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            return ToDecimal(cmd.ExecuteScalar());
        }
    }

    /// <inheritdoc />
    public void ResetDaily()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            Execute("DELETE FROM daily_spend;");
        }
    }

    /// <inheritdoc />
    public void ResetSession(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM session_spend WHERE session_id = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int EvictSessionsBefore(DateTime cutoff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string cutoffStr = FormatTimestamp(cutoff);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM session_spend WHERE updated_at < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", cutoffStr);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            Execute("DELETE FROM daily_spend;");
            Execute("DELETE FROM session_spend;");
            Execute("UPDATE total_spend SET amount = 0 WHERE id = 1;");
        }
    }

    /// <inheritdoc />
    public void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modelName);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO model_circuits (model_name, state, failure_count, cooldown_until)
                VALUES (@model, @state, @failures, @cooldown)
                ON CONFLICT(model_name) DO UPDATE SET
                    state = @state,
                    failure_count = @failures,
                    cooldown_until = @cooldown;
                """;
            cmd.Parameters.AddWithValue("@model", modelName);
            cmd.Parameters.AddWithValue("@state", state.ToString());
            cmd.Parameters.AddWithValue("@failures", failureCount);
            cmd.Parameters.AddWithValue("@cooldown", FormatTimestamp(cooldownUntil));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <inheritdoc />
    public Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)>(StringComparer.Ordinal);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT model_name, state, failure_count, cooldown_until FROM model_circuits;";
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
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string FormatDate(DateTime utc)
    {
        // UTC 日期键：yyyy-MM-dd。CultureInvariant 避免本地化。
        return utc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTime utc)
    {
        // ISO 8601 完整时间戳（UTC），用于 session_spend.updated_at 与淘汰比较。
        // 字典序与时间序一致，支持字符串比较淘汰。
        return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static decimal ToDecimal(object? value)
    {
        if (value is null || value == DBNull.Value) return 0m;
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }
}
