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

        // Default Timeout 连接串参数 = busy_timeout（秒），让 SQLite 在写锁竞争时等待而非立即抛 SQLITE_BUSY。
        // 两个 store（Cost + Audit）共享同一文件，各自独立连接，必须靠 busy_timeout 串行化跨 store 写。
        _connection = new SqliteConnection($"Data Source={path};Default Timeout=15");
        _connection.Open();

        // WAL 模式：提升并发读，崩溃恢复。busy_timeout PRAGMA 兜底（连接串 Default Timeout 已设）。
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=5000;");

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
            CREATE TABLE IF NOT EXISTS daily_spend_history (
                date TEXT PRIMARY KEY,
                amount REAL NOT NULL DEFAULT 0
            );
            """);

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
            // 仅清空当日累计。归档由 CostLedger.ResetDailyIfNewDay 在调用本方法前
            // 经 SnapshotDaily(_lastDailyDate) 完成（归档昨天），此处再归档会重复且日期错（今天）。
            Execute("DELETE FROM daily_spend;");
        }
    }

    /// <inheritdoc />
    public void SnapshotDaily(DateTime utcDate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            string todayKey = FormatDate(utcDate);
            // Only snapshot if there's actual spend for today.
            using var checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = "SELECT amount FROM daily_spend WHERE date = @date;";
            checkCmd.Parameters.AddWithValue("@date", todayKey);
            var result = checkCmd.ExecuteScalar();
            if (result is null || result == DBNull.Value) return;

            decimal amount = ToDecimal(result);
            if (amount == 0m) return;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO daily_spend_history (date, amount)
                VALUES (@date, @amount)
                ON CONFLICT(date) DO UPDATE SET amount = excluded.amount;
                """;
            cmd.Parameters.AddWithValue("@date", todayKey);
            cmd.Parameters.AddWithValue("@amount", (double)amount);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (days <= 0) return Array.Empty<(DateTime, decimal)>();

        lock (_lock)
        {
            var result = new List<(DateTime, decimal)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT date, amount FROM daily_spend_history
                WHERE date >= @cutoff
                ORDER BY date ASC;
                """;
            string cutoff = FormatDate(DateTime.UtcNow.AddDays(-days));
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string dateStr = reader.GetString(0);
                if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    result.Add((date, ToDecimal(reader.GetValue(1))));
                }
            }
            return result;
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
