using Npgsql;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 PostgreSQL 的分布式请求审计存储实现。
/// 供 Kubernetes 多节点 Pod 共享中心审计数据与全局延迟/失败聚合统计。
/// </summary>
public sealed class PostgresRequestAuditStore : IRequestAuditStore
{
    private readonly string? _connectionString;
    private readonly IRequestAuditStore _fallback;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private bool _disposed;

    /// <summary>
    /// 初始化 PostgreSQL 请求审计存储。
    /// </summary>
    public PostgresRequestAuditStore(string? connectionString, IRequestAuditStore? fallback = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _connectionString = connectionString;
        _fallback = fallback ?? new InMemoryRequestAuditStore();
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                EnsureTableCreated();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "PostgresRequestAuditStore 初始化失败（EnsureTableCreated），后续操作将降级到内存 fallback");
            }
        }
    }

    private void EnsureTableCreated()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS optirouter_request_audits (
    id BIGSERIAL PRIMARY KEY,
    timestamp TIMESTAMPTZ NOT NULL,
    request_id TEXT NULL,
    model TEXT NOT NULL,
    estimated_tokens INT NOT NULL,
    prompt_tokens INT NOT NULL,
    completion_tokens INT NOT NULL,
    cost NUMERIC NOT NULL,
    latency_ms BIGINT NOT NULL,
    session_id TEXT NULL,
    routing_reason TEXT NOT NULL,
    success BOOLEAN NOT NULL,
    error_message TEXT NULL,
    is_streaming BOOLEAN NOT NULL,
    routed_tier TEXT NOT NULL,
    cascade_triggered BOOLEAN NOT NULL,
    upgraded_from TEXT NULL,
    is_adopted BOOLEAN NOT NULL,
    parallel_group_id TEXT NULL,
    is_estimated BOOLEAN NOT NULL,
    fusion_role TEXT NULL,
    ttft_ms BIGINT NULL,
    cached_input_tokens INT NOT NULL DEFAULT 0,
    cache_write_input_tokens INT NOT NULL DEFAULT 0,
    uncached_input_tokens INT NOT NULL DEFAULT 0,
    quota_limited BOOLEAN NOT NULL DEFAULT FALSE,
    trace_id TEXT NULL,
    span_id TEXT NULL,
    parent_span_id TEXT NULL,
    reward DOUBLE PRECISION NULL,
    epsilon_promoted_model TEXT NULL,
    request_content TEXT NULL,
    classification_signal VARCHAR(64) NULL
);

CREATE INDEX IF NOT EXISTS idx_audits_timestamp ON optirouter_request_audits (timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_audits_model ON optirouter_request_audits (model);
";
        cmd.ExecuteNonQuery();

        // 增量列迁移：旧表可能缺少后续新增列，用 ADD COLUMN IF NOT EXISTS 补加（Postgres 9.6+）。
        EnsureColumn("cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("cache_write_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("uncached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("quota_limited", "BOOLEAN NOT NULL DEFAULT FALSE");
        EnsureColumn("trace_id", "TEXT");
        EnsureColumn("span_id", "TEXT");
        EnsureColumn("parent_span_id", "TEXT");
        EnsureColumn("reward", "DOUBLE PRECISION");
        EnsureColumn("epsilon_promoted_model", "TEXT");
        EnsureColumn("request_content", "TEXT");
        EnsureColumn("classification_signal", "VARCHAR(64)");
    }

    // 信任边界守卫：EnsureColumn 用插值拼 DDL（标识符无法参数化）。列名必须是纯小写
    // 标识符；DDL 片段禁止多语句/注释注入字符。当前调用点全为硬编码字面量，
    // 此校验防止后续维护者把外部输入传进来（构造方 try/catch 会转降级而非崩溃）。
    private static readonly System.Text.RegularExpressions.Regex SafeColumnIdentifier =
        new("^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void EnsureColumn(string columnName, string definition)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;

        if (!SafeColumnIdentifier.IsMatch(columnName)
            || definition.Contains(';') || definition.Contains("--"))
        {
            throw new ArgumentException(
                $"Refusing to execute DDL migration for column '{columnName}': identifier/definition failed the safety whitelist.");
        }

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE optirouter_request_audits ADD COLUMN IF NOT EXISTS {columnName} {definition};";
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Append(RequestAuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) { _fallback.Append(record); return; }

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO optirouter_request_audits
(timestamp, request_id, model, estimated_tokens, prompt_tokens, completion_tokens, cost, latency_ms, session_id, routing_reason, success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from, is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms, cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited, trace_id, span_id, parent_span_id, reward, epsilon_promoted_model, request_content, classification_signal)
VALUES
(@ts, @rid, @model, @est, @ptok, @ctok, @cost, @lat, @sid, @reason, @succ, @err, @stream, @rtier, @cascade, @upg, @adopted, @pgid, @estim, @frole, @ttft, @cached, @cachewrite, @uncached, @quota, @trace, @span, @parent, @reward, @epsilon, @reqcontent, @csignal);
";
            cmd.Parameters.AddWithValue("ts", record.Timestamp);
            cmd.Parameters.AddWithValue("rid", (object?)record.RequestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("model", record.Model);
            cmd.Parameters.AddWithValue("est", record.EstimatedInputTokens);
            cmd.Parameters.AddWithValue("ptok", record.PromptTokens);
            cmd.Parameters.AddWithValue("ctok", record.CompletionTokens);
            cmd.Parameters.AddWithValue("cost", record.Cost);
            cmd.Parameters.AddWithValue("lat", record.LatencyMs);
            cmd.Parameters.AddWithValue("sid", (object?)record.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reason", record.RoutingReason);
            cmd.Parameters.AddWithValue("succ", record.Success);
            cmd.Parameters.AddWithValue("err", (object?)record.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("stream", record.IsStreaming);
            cmd.Parameters.AddWithValue("rtier", record.RoutedTier.ToString());
            cmd.Parameters.AddWithValue("cascade", record.CascadeTriggered);
            cmd.Parameters.AddWithValue("upg", (object?)record.UpgradedFrom ?? DBNull.Value);
            cmd.Parameters.AddWithValue("adopted", record.IsAdopted);
            cmd.Parameters.AddWithValue("pgid", (object?)record.ParallelGroupId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("estim", record.IsEstimated);
            cmd.Parameters.AddWithValue("frole", (object?)record.FusionRole ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ttft", (object?)record.TimeToFirstTokenMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("cached", record.CachedInputTokens);
            cmd.Parameters.AddWithValue("cachewrite", record.CacheWriteInputTokens);
            cmd.Parameters.AddWithValue("uncached", record.UncachedInputTokens);
            cmd.Parameters.AddWithValue("quota", record.QuotaLimited);
            cmd.Parameters.AddWithValue("trace", (object?)record.TraceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("span", (object?)record.SpanId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("parent", (object?)record.ParentSpanId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reward", (object?)record.Reward ?? DBNull.Value);
            cmd.Parameters.AddWithValue("epsilon", (object?)record.EpsilonPromotedModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reqcontent", (object?)record.RequestContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("csignal", (object?)record.ClassificationSignal ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.Append 失败，降级到内存 fallback");
            _fallback.Append(record);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RequestAuditRecord> GetRecent(int limit)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetRecent(limit);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM optirouter_request_audits ORDER BY timestamp DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("limit", limit);

            return ReadRecords(cmd);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetRecent 失败，降级到内存 fallback");
            return _fallback.GetRecent(limit);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RequestAuditRecord> GetByModel(string modelName, int limit)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetByModel(modelName, limit);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM optirouter_request_audits WHERE model = @model ORDER BY timestamp DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("model", modelName);
            cmd.Parameters.AddWithValue("limit", limit);

            return ReadRecords(cmd);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetByModel 失败，降级到内存 fallback");
            return _fallback.GetByModel(modelName, limit);
        }
    }

    /// <inheritdoc />
    public (IReadOnlyList<RequestAuditRecord> Items, int TotalCount) GetByTimeRange(DateTime from, DateTime to, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetByTimeRange(from, to, limit, offset);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM optirouter_request_audits WHERE timestamp >= @from AND timestamp <= @to;";
            countCmd.Parameters.AddWithValue("from", from);
            countCmd.Parameters.AddWithValue("to", to);
            int total = Convert.ToInt32(countCmd.ExecuteScalar());

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM optirouter_request_audits WHERE timestamp >= @from AND timestamp <= @to ORDER BY timestamp DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("to", to);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);

            var items = ReadRecords(cmd);
            return (items, total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetByTimeRange 失败，降级到内存 fallback");
            return _fallback.GetByTimeRange(from, to, limit, offset);
        }
    }

    /// <inheritdoc />
    public (int Failures, int Total) GetFailureStats(DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetFailureStats(from, to);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT 
    COUNT(CASE WHEN success = FALSE THEN 1 END),
    COUNT(*)
FROM optirouter_request_audits
WHERE timestamp >= @from AND timestamp <= @to;
";
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("to", to);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int fail = Convert.ToInt32(reader.GetValue(0));
                int total = Convert.ToInt32(reader.GetValue(1));
                return (fail, total);
            }
            return (0, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetFailureStats 失败，降级到内存 fallback");
            return _fallback.GetFailureStats(from, to);
        }
    }

    /// <inheritdoc />
    public WindowAggregateStats GetAggregateStats(DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetAggregateStats(from, to);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT 
    COUNT(*),
    COUNT(CASE WHEN success = FALSE THEN 1 END),
    COALESCE(SUM(prompt_tokens), 0),
    COALESCE(SUM(completion_tokens), 0),
    COALESCE(SUM(cached_input_tokens), 0),
    COALESCE(SUM(cache_write_input_tokens), 0),
    COALESCE(SUM(uncached_input_tokens), 0),
    COALESCE(SUM(CASE WHEN success = TRUE THEN latency_ms ELSE 0 END), 0),
    COUNT(CASE WHEN success = TRUE THEN 1 END),
    COALESCE(SUM(cost), 0)
FROM optirouter_request_audits
WHERE timestamp >= @from AND timestamp <= @to;
";
            cmd.Parameters.AddWithValue("from", from == DateTime.MinValue ? DateTime.MinValue : from);
            cmd.Parameters.AddWithValue("to", to);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new WindowAggregateStats(
                    TotalRequests: Convert.ToInt32(reader.GetValue(0)),
                    Failures: Convert.ToInt32(reader.GetValue(1)),
                    InputTokens: Convert.ToInt64(reader.GetValue(2)),
                    OutputTokens: Convert.ToInt64(reader.GetValue(3)),
                    CachedInputTokens: Convert.ToInt64(reader.GetValue(4)),
                    CacheWriteInputTokens: Convert.ToInt64(reader.GetValue(5)),
                    UncachedInputTokens: Convert.ToInt64(reader.GetValue(6)),
                    SuccessLatencySumMs: Convert.ToInt64(Math.Round(Convert.ToDouble(reader.GetValue(7)))),
                    SuccessLatencySamples: Convert.ToInt32(reader.GetValue(8)),
                    TotalCost: Convert.ToDouble(reader.GetValue(9))
                );
            }

            return new WindowAggregateStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetAggregateStats 失败，降级到内存 fallback");
            return _fallback.GetAggregateStats(from, to);
        }
    }

    /// <inheritdoc />
    public int EvictBefore(DateTime cutoff)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.EvictBefore(cutoff);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_request_audits WHERE timestamp < @cutoff;";
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.EvictBefore 失败，降级到内存 fallback");
            return _fallback.EvictBefore(cutoff);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ModelLatencyStats> GetLatencyStatsSince(DateTime since)
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return _fallback.GetLatencyStatsSince(since);

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT model, AVG(latency_ms), percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms), COUNT(*)
FROM optirouter_request_audits
WHERE success = TRUE AND timestamp >= @since
GROUP BY model;
";
            cmd.Parameters.AddWithValue("since", since);

            var dict = new Dictionary<string, ModelLatencyStats>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string model = reader.GetString(0);
                double avg = reader.GetDouble(1);
                double p95 = reader.IsDBNull(2) ? avg : reader.GetDouble(2);
                int count = Convert.ToInt32(reader.GetValue(3));
                dict[model] = new ModelLatencyStats(avg, p95, count);
            }
            return dict;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PostgresRequestAuditStore.GetLatencyStatsSince 失败，降级到内存 fallback");
            return _fallback.GetLatencyStatsSince(since);
        }
    }

    private static List<RequestAuditRecord> ReadRecords(NpgsqlCommand cmd)
    {
        var records = new List<RequestAuditRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            Enum.TryParse<ModelTier>(reader.GetString(reader.GetOrdinal("routed_tier")), out var tier);

            records.Add(new RequestAuditRecord(
                Timestamp: reader.GetDateTime(reader.GetOrdinal("timestamp")),
                RequestId: reader.IsDBNull(reader.GetOrdinal("request_id")) ? null : reader.GetString(reader.GetOrdinal("request_id")),
                Model: reader.GetString(reader.GetOrdinal("model")),
                EstimatedInputTokens: reader.GetInt32(reader.GetOrdinal("estimated_tokens")),
                PromptTokens: reader.GetInt32(reader.GetOrdinal("prompt_tokens")),
                CompletionTokens: reader.GetInt32(reader.GetOrdinal("completion_tokens")),
                Cost: reader.GetDecimal(reader.GetOrdinal("cost")),
                LatencyMs: reader.GetInt64(reader.GetOrdinal("latency_ms")),
                SessionId: reader.IsDBNull(reader.GetOrdinal("session_id")) ? null : reader.GetString(reader.GetOrdinal("session_id")),
                RoutingReason: reader.GetString(reader.GetOrdinal("routing_reason")),
                Success: reader.GetBoolean(reader.GetOrdinal("success")),
                ErrorMessage: reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
                IsStreaming: reader.GetBoolean(reader.GetOrdinal("is_streaming")),
                RoutedTier: tier,
                CascadeTriggered: reader.GetBoolean(reader.GetOrdinal("cascade_triggered")),
                UpgradedFrom: reader.IsDBNull(reader.GetOrdinal("upgraded_from")) ? null : reader.GetString(reader.GetOrdinal("upgraded_from")),
                IsAdopted: reader.GetBoolean(reader.GetOrdinal("is_adopted")),
                ParallelGroupId: reader.IsDBNull(reader.GetOrdinal("parallel_group_id")) ? null : reader.GetString(reader.GetOrdinal("parallel_group_id")),
                IsEstimated: reader.GetBoolean(reader.GetOrdinal("is_estimated")),
                FusionRole: reader.IsDBNull(reader.GetOrdinal("fusion_role")) ? null : reader.GetString(reader.GetOrdinal("fusion_role")),
                TimeToFirstTokenMs: reader.IsDBNull(reader.GetOrdinal("ttft_ms")) ? null : reader.GetInt64(reader.GetOrdinal("ttft_ms")),
                CachedInputTokens: reader.GetInt32(reader.GetOrdinal("cached_input_tokens")),
                CacheWriteInputTokens: reader.GetInt32(reader.GetOrdinal("cache_write_input_tokens")),
                UncachedInputTokens: reader.GetInt32(reader.GetOrdinal("uncached_input_tokens")),
                QuotaLimited: reader.GetBoolean(reader.GetOrdinal("quota_limited")),
                TraceId: reader.IsDBNull(reader.GetOrdinal("trace_id")) ? null : reader.GetString(reader.GetOrdinal("trace_id")),
                SpanId: reader.IsDBNull(reader.GetOrdinal("span_id")) ? null : reader.GetString(reader.GetOrdinal("span_id")),
                ParentSpanId: reader.IsDBNull(reader.GetOrdinal("parent_span_id")) ? null : reader.GetString(reader.GetOrdinal("parent_span_id")),
                Reward: reader.IsDBNull(reader.GetOrdinal("reward")) ? null : reader.GetDouble(reader.GetOrdinal("reward")),
                EpsilonPromotedModel: reader.IsDBNull(reader.GetOrdinal("epsilon_promoted_model")) ? null : reader.GetString(reader.GetOrdinal("epsilon_promoted_model")),
                RequestContent: reader.IsDBNull(reader.GetOrdinal("request_content")) ? null : reader.GetString(reader.GetOrdinal("request_content")),
                ClassificationSignal: reader.IsDBNull(reader.GetOrdinal("classification_signal")) ? null : reader.GetString(reader.GetOrdinal("classification_signal"))
            ));
        }
        return records;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fallback.Dispose();
    }
}
