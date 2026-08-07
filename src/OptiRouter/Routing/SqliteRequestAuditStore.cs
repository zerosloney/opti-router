using System.Globalization;
using Microsoft.Data.Sqlite;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// SQLite 持久化的请求审计存储，线程安全。
/// </summary>
public sealed class SqliteRequestAuditStore : IRequestAuditStore, IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>
    /// 用指定 DB 文件路径构造。
    /// </summary>
    /// <param name="path">SQLite 文件路径。与 CostLedger 共用同一文件。</param>
    public SqliteRequestAuditStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");

        Execute("""
            CREATE TABLE IF NOT EXISTS request_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                request_id TEXT NOT NULL,
                model TEXT NOT NULL,
                estimated_tokens INTEGER NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                cost REAL NOT NULL DEFAULT 0,
                latency_ms INTEGER NOT NULL DEFAULT 0,
                session_id TEXT,
                routing_reason TEXT NOT NULL,
                success INTEGER NOT NULL,
                error_message TEXT,
                is_streaming INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_request_audit_timestamp ON request_audit(timestamp);");
        Execute("CREATE INDEX IF NOT EXISTS idx_request_audit_model ON request_audit(model);");

        // 增量列迁移：旧 DB（无 routed_tier/cascade_triggered/upgraded_from）需补列。
        // SQLite 不支持 ADD COLUMN IF NOT EXISTS，用 PRAGMA table_info 探测后补加。
        EnsureColumn("routed_tier", "TEXT");
        EnsureColumn("cascade_triggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("upgraded_from", "TEXT");
    }

    private void EnsureColumn(string columnName, string definition)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(request_audit);";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    existing.Add(reader.GetString(1)); // column name is field 1
            }

            if (!existing.Contains(columnName))
            {
                Execute($"ALTER TABLE request_audit ADD COLUMN {columnName} {definition};");
            }
        }
    }

    /// <inheritdoc />
    public void Append(RequestAuditRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO request_audit
                    (timestamp, request_id, model, estimated_tokens, prompt_tokens,
                     completion_tokens, cost, latency_ms, session_id, routing_reason,
                     success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from)
                VALUES
                    (@ts, @rid, @model, @est, @ptok, @ctok, @cost, @lat, @sid, @reason, @succ, @err, @stream,
                     @rtier, @cascade, @upg);
                """;
            cmd.Parameters.AddWithValue("@ts", FormatTimestamp(record.Timestamp));
            cmd.Parameters.AddWithValue("@rid", record.RequestId);
            cmd.Parameters.AddWithValue("@model", record.Model);
            cmd.Parameters.AddWithValue("@est", record.EstimatedInputTokens);
            cmd.Parameters.AddWithValue("@ptok", record.PromptTokens);
            cmd.Parameters.AddWithValue("@ctok", record.CompletionTokens);
            cmd.Parameters.AddWithValue("@cost", (double)record.Cost);
            cmd.Parameters.AddWithValue("@lat", record.LatencyMs);
            cmd.Parameters.AddWithValue("@sid", (object?)record.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason", record.RoutingReason);
            cmd.Parameters.AddWithValue("@succ", record.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("@err", (object?)record.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stream", record.IsStreaming ? 1 : 0);
            cmd.Parameters.AddWithValue("@rtier", record.RoutedTier.ToString());
            cmd.Parameters.AddWithValue("@cascade", record.CascadeTriggered ? 1 : 0);
            cmd.Parameters.AddWithValue("@upg", (object?)record.UpgradedFrom ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RequestAuditRecord> GetRecent(int limit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (limit <= 0) return Array.Empty<RequestAuditRecord>();

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
                       completion_tokens, cost, latency_ms, session_id, routing_reason,
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from
                FROM request_audit
                ORDER BY id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", limit);
            return ReadAll(cmd);
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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
                       completion_tokens, cost, latency_ms, session_id, routing_reason,
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from
                FROM request_audit
                WHERE model = @model
                ORDER BY id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@model", modelName);
            cmd.Parameters.AddWithValue("@limit", limit);
            return ReadAll(cmd);
        }
    }

    /// <inheritdoc />
    public (IReadOnlyList<RequestAuditRecord> Items, int TotalCount) GetByTimeRange(
        DateTime from, DateTime to, int limit, int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (limit <= 0) return (Array.Empty<RequestAuditRecord>(), 0);
        if (offset < 0) offset = 0;

        lock (_lock)
        {
            // 先取总数。
            using var countCmd = _connection.CreateCommand();
            countCmd.CommandText = """
                SELECT COUNT(*) FROM request_audit
                WHERE timestamp >= @from AND timestamp <= @to;
                """;
            countCmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
            countCmd.Parameters.AddWithValue("@to", FormatTimestamp(to));
            int totalCount = Convert.ToInt32(countCmd.ExecuteScalar());

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
                       completion_tokens, cost, latency_ms, session_id, routing_reason,
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from
                FROM request_audit
                WHERE timestamp >= @from AND timestamp <= @to
                ORDER BY id DESC
                LIMIT @limit OFFSET @offset;
                """;
            cmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
            cmd.Parameters.AddWithValue("@to", FormatTimestamp(to));
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            var items = ReadAll(cmd);
            return (items, totalCount);
        }
    }

    /// <inheritdoc />
    public int EvictBefore(DateTime cutoff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM request_audit WHERE timestamp < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", FormatTimestamp(cutoff));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private List<RequestAuditRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<RequestAuditRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RequestAuditRecord(
                Timestamp: DateTime.ParseExact(reader.GetString(0), "o", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                RequestId: reader.GetString(1),
                Model: reader.GetString(2),
                EstimatedInputTokens: reader.GetInt32(3),
                PromptTokens: reader.GetInt32(4),
                CompletionTokens: reader.GetInt32(5),
                Cost: ToDecimal(reader.GetValue(6)),
                LatencyMs: reader.GetInt64(7),
                SessionId: reader.IsDBNull(8) ? null : reader.GetString(8),
                RoutingReason: reader.GetString(9),
                Success: reader.GetInt32(10) != 0,
                ErrorMessage: reader.IsDBNull(11) ? null : reader.GetString(11),
                IsStreaming: reader.GetInt32(12) != 0,
                RoutedTier: reader.IsDBNull(13) ? ModelTier.Medium : Enum.Parse<ModelTier>(reader.GetString(13), ignoreCase: true),
                CascadeTriggered: reader.IsDBNull(14) ? false : reader.GetInt32(14) != 0,
                UpgradedFrom: reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        return list;
    }

    private static string FormatTimestamp(DateTime utc)
        => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value)
    {
        if (value is null || value == DBNull.Value) return 0m;
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
