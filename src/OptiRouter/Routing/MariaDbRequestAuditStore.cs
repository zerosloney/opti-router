using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// MariaDB/MySQL 持久化的请求审计存储，线程安全。
/// 与 <see cref="SqliteRequestAuditStore"/> 同构：零阻塞入列 + 后台批量写事务，
/// 仅连接管理改为每批从连接池取连接（MySqlConnector 默认池化）。
/// </summary>
public sealed class MariaDbRequestAuditStore : IRequestAuditStore, IDisposable
{
    private readonly object _lock = new();
    private readonly string _connectionString;
    private readonly ILogger<MariaDbRequestAuditStore> _logger;
    private int _consecutiveFlushFailures;
    private bool _disposed;

    private readonly ConcurrentQueue<RequestAuditRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;

    // Test-only seam: invoked after a batch is inserted and before its transaction commits.
    // Keeping it internal avoids expanding the IRequestAuditStore/public store contract.
    internal Action? BeforeAuditBatchCommitHook { get; set; }

    /// <summary>
    /// 用 MariaDB 连接串构造。
    /// </summary>
    /// <param name="connectionString">MariaDB 连接串。</param>
    /// <param name="logger">日志记录器（可选；默认 NullLogger），用于记录后台写入失败以免审计子系统静默死亡。</param>
    public MariaDbRequestAuditStore(string connectionString, ILogger<MariaDbRequestAuditStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<MariaDbRequestAuditStore>.Instance;

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS optirouter_request_audit (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                timestamp VARCHAR(64) NOT NULL,
                request_id VARCHAR(128) NOT NULL,
                model VARCHAR(255) NOT NULL,
                estimated_tokens INT NOT NULL,
                prompt_tokens INT NOT NULL DEFAULT 0,
                completion_tokens INT NOT NULL DEFAULT 0,
                cost DOUBLE NOT NULL DEFAULT 0,
                latency_ms BIGINT NOT NULL DEFAULT 0,
                session_id VARCHAR(255) NULL,
                routing_reason TEXT NOT NULL,
                success TINYINT NOT NULL,
                error_message LONGTEXT NULL,
                is_streaming TINYINT NOT NULL DEFAULT 0,
                routed_tier VARCHAR(32) NULL,
                cascade_triggered TINYINT NOT NULL DEFAULT 0,
                upgraded_from VARCHAR(255) NULL,
                is_adopted TINYINT NOT NULL DEFAULT 1,
                parallel_group_id VARCHAR(128) NULL,
                is_estimated TINYINT NOT NULL DEFAULT 0,
                fusion_role VARCHAR(32) NULL,
                ttft_ms BIGINT NULL,
                cached_input_tokens INT NOT NULL DEFAULT 0,
                cache_write_input_tokens INT NOT NULL DEFAULT 0,
                uncached_input_tokens INT NOT NULL DEFAULT 0,
                quota_limited TINYINT NOT NULL DEFAULT 0,
                trace_id VARCHAR(64) NULL,
                span_id VARCHAR(64) NULL,
                parent_span_id VARCHAR(64) NULL,
                reward DOUBLE NULL,
                epsilon_promoted_model VARCHAR(255) NULL,
                request_content LONGTEXT NULL,
                classification_signal VARCHAR(64) NULL
            );
            """);
        // 存量表补列（CREATE TABLE IF NOT EXISTS 不会更新既有表结构）。
        Execute(conn, "ALTER TABLE optirouter_request_audit ADD COLUMN IF NOT EXISTS classification_signal VARCHAR(64) NULL;");
        EnsureIndex(conn, "idx_or_audit_timestamp", "CREATE INDEX idx_or_audit_timestamp ON optirouter_request_audit(timestamp);");
        EnsureIndex(conn, "idx_or_audit_model", "CREATE INDEX idx_or_audit_model ON optirouter_request_audit(model);");

        _processTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>MariaDB 不支持 CREATE INDEX IF NOT EXISTS，经 information_schema 探测后补建。</summary>
    private static void EnsureIndex(MySqlConnection conn, string indexName, string createSql)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'optirouter_request_audit' AND INDEX_NAME = @idx;
                """;
            check.Parameters.AddWithValue("@idx", indexName);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        try
        {
            Execute(conn, createSql);
        }
        catch (MySqlException ex) when (ex.Message.Contains("Duplicate key name", StringComparison.OrdinalIgnoreCase))
        {
            // Ignore duplicate index error if another instance created it in parallel.
        }
    }

    /// <inheritdoc />
    public void Append(RequestAuditRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        // 零阻塞入列并唤醒后台批量写任务
        _queue.Enqueue(record);
        _signal.Release();
    }

    private async Task ProcessQueueAsync()
    {
        // 等待信号与排空队列分开捕获：FlushQueue 的 MySqlException/网络错误不能终结后台任务，
        // 否则 _processTask 静默 fault、队列无限增长、每个 reader 的 FlushQueue 重抛，审计子系统无声死亡。
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                FlushQueue();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit FlushQueue failed; requeued batch for retry");
            }
        }

        // 取消退出前尽力排空剩余记录。
        try { FlushQueue(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Final audit FlushQueue on shutdown failed"); }
    }

    private void FlushQueue()
    {
        lock (_lock)
        {
            if (_queue.IsEmpty) return;

            var batch = new List<RequestAuditRecord>();
            while (_queue.TryDequeue(out var record))
                batch.Add(record);

            if (batch.Count == 0) return;

            try
            {
                using var conn = new MySqlConnection(_connectionString);
                conn.Open();
                using var tx = conn.BeginTransaction();
                foreach (var record in batch)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO optirouter_request_audit
                            (timestamp, request_id, model, estimated_tokens, prompt_tokens,
                             completion_tokens, cost, latency_ms, session_id, routing_reason,
                             success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
                             is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
                             cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
                             trace_id, span_id, parent_span_id, reward, epsilon_promoted_model, request_content, classification_signal)
                        VALUES
                            (@ts, @rid, @model, @est, @ptok, @ctok, @cost, @lat, @sid, @reason, @succ, @err, @stream,
                             @rtier, @cascade, @upg, @adopted, @pgid, @estim, @frole, @ttft,
                             @cached, @cachewrite, @uncached, @quota, @trace, @span, @parent, @reward, @epsilon, @reqcontent, @csignal);
                        """;
                    cmd.Parameters.AddWithValue("@ts", FormatTimestamp(record.Timestamp));
                    cmd.Parameters.AddWithValue("@rid", record.RequestId ?? string.Empty);
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
                    cmd.Parameters.AddWithValue("@adopted", record.IsAdopted ? 1 : 0);
                    cmd.Parameters.AddWithValue("@pgid", (object?)record.ParallelGroupId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@estim", record.IsEstimated ? 1 : 0);
                    cmd.Parameters.AddWithValue("@frole", (object?)record.FusionRole ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ttft", (object?)record.TimeToFirstTokenMs ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cached", record.CachedInputTokens);
                    cmd.Parameters.AddWithValue("@cachewrite", record.CacheWriteInputTokens);
                    cmd.Parameters.AddWithValue("@uncached", record.UncachedInputTokens);
                    cmd.Parameters.AddWithValue("@quota", record.QuotaLimited ? 1 : 0);
                    cmd.Parameters.AddWithValue("@trace", (object?)record.TraceId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@span", (object?)record.SpanId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@parent", (object?)record.ParentSpanId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@reward", (object?)record.Reward ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@epsilon", (object?)record.EpsilonPromotedModel ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@reqcontent", (object?)record.RequestContent ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@csignal", (object?)record.ClassificationSignal ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                BeforeAuditBatchCommitHook?.Invoke();
                tx.Commit();
            }
            catch (Exception ex)
            {
                // Commit may be ambiguous; replaying is safer than losing audit records.
                // 但持久性故障（MariaDB 不可达）下无限重试会刷屏并卡住 GetRecent：
                // 连续 5 批失败后丢弃该批并记 Error，下批重新计数（审计尽力而为）。
                if (++_consecutiveFlushFailures >= 5)
                {
                    _logger.LogError(ex, "Audit flush failed {Failures} consecutive batches; dropping {Count} audit records to unblock the queue",
                        _consecutiveFlushFailures, batch.Count);
                    _consecutiveFlushFailures = 0;
                    return;
                }
                foreach (var record in batch)
                    _queue.Enqueue(record);
                _signal.Release(batch.Count);
                throw;
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
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectColumnsSql + " ORDER BY id DESC LIMIT @limit;";
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
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectColumnsSql + " WHERE model = @model ORDER BY id DESC LIMIT @limit;";
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
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            // 先取总数（分页依赖真实总数，不随 limit 变化）。
            int totalCount;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = """
                    SELECT COUNT(*) FROM optirouter_request_audit
                    WHERE timestamp >= @from AND timestamp <= @to;
                    """;
                countCmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
                countCmd.Parameters.AddWithValue("@to", FormatTimestamp(to));
                totalCount = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectColumnsSql + " WHERE timestamp >= @from AND timestamp <= @to ORDER BY id DESC LIMIT @limit OFFSET @offset;";
            cmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
            cmd.Parameters.AddWithValue("@to", FormatTimestamp(to));
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            var items = ReadAll(cmd);
            return (items, totalCount);
        }
    }

    /// <inheritdoc />
    public (int Failures, int Total) GetFailureStats(DateTime from, DateTime to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 单条聚合：SUM(CASE...) 统计失败数，COUNT(*) 统计总数。
            cmd.CommandText = """
                SELECT COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0),
                       COUNT(*)
                FROM optirouter_request_audit
                WHERE timestamp >= @from AND timestamp <= @to;
                """;
            cmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
            cmd.Parameters.AddWithValue("@to", FormatTimestamp(to));

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int failures = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                int total = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                return (failures, total);
            }
            return (0, 0);
        }
    }

    /// <inheritdoc />
    public WindowAggregateStats GetAggregateStats(DateTime from, DateTime to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            // 单条聚合查询，与 GetFailureStats 同模式：O(1) 内存，走 timestamp 索引。
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(prompt_tokens), 0),
                    COALESCE(SUM(completion_tokens), 0),
                    COALESCE(SUM(cached_input_tokens), 0),
                    COALESCE(SUM(cache_write_input_tokens), 0),
                    COALESCE(SUM(uncached_input_tokens), 0),
                    COALESCE(SUM(CASE WHEN success = 1 THEN latency_ms ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(cost), 0)
                FROM optirouter_request_audit
                WHERE timestamp >= @from AND timestamp <= @to;
                """;
            cmd.Parameters.AddWithValue("@from", FormatTimestamp(from));
            cmd.Parameters.AddWithValue("@to", FormatTimestamp(to));

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int total = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                int failures = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                long inputTokens = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                long outputTokens = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                long cached = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                long cacheWrite = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                long uncached = Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture);
                long latSum = Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
                int latSamples = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture);
                double totalCost = Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture);
                return new WindowAggregateStats(total, failures, inputTokens, outputTokens, cached, cacheWrite, uncached, latSum, latSamples, totalCost);
            }
            return new WindowAggregateStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0);
        }
    }

    /// <inheritdoc />
    public int EvictBefore(DateTime cutoff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            FlushQueue();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM optirouter_request_audit WHERE timestamp < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", FormatTimestamp(cutoff));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ModelLatencyStats> GetLatencyStatsSince(DateTime since)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            FlushQueue();
            // 需计算 p95，MariaDB 无直接百分位聚合。逐行拉取窗口内成功延迟，
            // C# 侧分组排序算 avg + p95。窗口（默认 60min）内模型 <50、样本有界，后台低频聚合可接受。
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT model, latency_ms
                FROM optirouter_request_audit
                WHERE timestamp >= @since AND success = 1
                ORDER BY model;
                """;
            cmd.Parameters.AddWithValue("@since", FormatTimestamp(since));

            var byModel = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    string model = reader.GetString(0);
                    double lat = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
                    if (!byModel.TryGetValue(model, out var list))
                    {
                        list = new List<double>();
                        byModel[model] = list;
                    }
                    list.Add(lat);
                }
            }

            var result = new Dictionary<string, ModelLatencyStats>(byModel.Count, StringComparer.Ordinal);
            foreach (var (model, lats) in byModel)
            {
                lats.Sort();
                double avg = lats.Count == 0 ? 0.0 : lats.Sum() / lats.Count;
                result[model] = new ModelLatencyStats(avg, LatencyStatsMath.Percentile(lats, 95.0), lats.Count);
            }
            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            _processTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore cancel exception during dispose
        }

        lock (_lock)
        {
            FlushQueue();
        }
        _signal.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    // 注意：常量末尾无空白，拼接 WHERE/ORDER 子句时需以空格开头。
    private const string SelectColumnsSql = """
        SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
               completion_tokens, cost, latency_ms, session_id, routing_reason,
               success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
               is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
               cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
               trace_id, span_id, parent_span_id, reward, epsilon_promoted_model, request_content, classification_signal
        FROM optirouter_request_audit
        """;

    private static List<RequestAuditRecord> ReadAll(MySqlCommand cmd)
    {
        var list = new List<RequestAuditRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RequestAuditRecord(
                Timestamp: DateTime.ParseExact(reader.GetString(0), "o", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                RequestId: string.IsNullOrEmpty(reader.GetString(1)) ? null : reader.GetString(1),
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
                UpgradedFrom: reader.IsDBNull(15) ? null : reader.GetString(15),
                IsAdopted: reader.IsDBNull(16) ? true : reader.GetInt32(16) != 0,
                ParallelGroupId: reader.IsDBNull(17) ? null : reader.GetString(17),
                IsEstimated: reader.IsDBNull(18) ? false : reader.GetInt32(18) != 0,
                FusionRole: reader.IsDBNull(19) ? null : reader.GetString(19),
                TimeToFirstTokenMs: reader.IsDBNull(20) ? null : reader.GetInt64(20),
                CachedInputTokens: reader.IsDBNull(21) ? 0 : reader.GetInt32(21),
                CacheWriteInputTokens: reader.IsDBNull(22) ? 0 : reader.GetInt32(22),
                UncachedInputTokens: reader.IsDBNull(23) ? 0 : reader.GetInt32(23),
                QuotaLimited: !reader.IsDBNull(24) && reader.GetInt32(24) != 0,
                TraceId: reader.IsDBNull(25) ? null : reader.GetString(25),
                SpanId: reader.IsDBNull(26) ? null : reader.GetString(26),
                ParentSpanId: reader.IsDBNull(27) ? null : reader.GetString(27),
                Reward: reader.IsDBNull(28) ? null : (double?)reader.GetDouble(28),
                EpsilonPromotedModel: reader.IsDBNull(29) ? null : reader.GetString(29),
                RequestContent: reader.IsDBNull(30) ? null : reader.GetString(30),
                ClassificationSignal: reader.IsDBNull(31) ? null : reader.GetString(31)));
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

    private static void Execute(MySqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
