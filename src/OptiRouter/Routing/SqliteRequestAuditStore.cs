using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OptiRouter.Tests")]

namespace OptiRouter.Routing;

/// <summary>
/// SQLite 持久化的请求审计存储，线程安全。
/// </summary>
public sealed class SqliteRequestAuditStore : IRequestAuditStore, IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteRequestAuditStore> _logger;
    private bool _disposed;

    private readonly ConcurrentQueue<RequestAuditRecord> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;

    // Test-only seam: invoked after a batch is inserted and before its transaction commits.
    // Keeping it internal avoids expanding the IRequestAuditStore/public store contract.
    internal Action? BeforeAuditBatchCommitHook { get; set; }

    /// <summary>
    /// 用指定 DB 文件路径构造。
    /// </summary>
    /// <param name="path">SQLite 文件路径。与 CostLedger 共用同一文件。</param>
    /// <param name="logger">日志记录器（可选；默认 NullLogger），用于记录后台写入失败以免审计子系统静默死亡。</param>
    public SqliteRequestAuditStore(string path, ILogger<SqliteRequestAuditStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _logger = logger ?? NullLogger<SqliteRequestAuditStore>.Instance;

        // Default Timeout 连接串参数 = busy_timeout（秒）；与 SqliteCostLedgerStore 共享同一文件需跨 store 写串行化。
        _connection = new SqliteConnection($"Data Source={path};Default Timeout=15");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");

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
        // 并行首试审计字段（向后兼容：旧记录 is_adopted=1、parallel_group_id=NULL）。
        EnsureColumn("is_adopted", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn("parallel_group_id", "TEXT");
        // 预估成本标记（向后兼容：旧记录 is_estimated=0）。
        EnsureColumn("is_estimated", "INTEGER NOT NULL DEFAULT 0");
        // 融合路由角色（向后兼容：旧记录/普通请求为 NULL）。
        EnsureColumn("fusion_role", "TEXT");
        EnsureColumn("ttft_ms", "INTEGER");
        EnsureColumn("cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("cache_write_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("uncached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("quota_limited", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("trace_id", "TEXT");
        EnsureColumn("span_id", "TEXT");
        EnsureColumn("parent_span_id", "TEXT");
        EnsureColumn("reward", "REAL");
        EnsureColumn("epsilon_promoted_model", "TEXT");

        _processTask = Task.Run(ProcessQueueAsync);
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

        // 零阻塞入列并唤醒后台批量写任务
        _queue.Enqueue(record);
        _signal.Release();
    }

    private async Task ProcessQueueAsync()
    {
        // 等待信号与排空队列分开捕获：FlushQueue 的 SqliteException/磁盘错误不能终结后台任务，
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
                using var tx = _connection.BeginTransaction();
                foreach (var record in batch)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO request_audit
                            (timestamp, request_id, model, estimated_tokens, prompt_tokens,
                             completion_tokens, cost, latency_ms, session_id, routing_reason,
                             success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
                             is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
                             cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
                             trace_id, span_id, parent_span_id, reward, epsilon_promoted_model)
                        VALUES
                            (@ts, @rid, @model, @est, @ptok, @ctok, @cost, @lat, @sid, @reason, @succ, @err, @stream,
                             @rtier, @cascade, @upg, @adopted, @pgid, @estim, @frole, @ttft,
                             @cached, @cachewrite, @uncached, @quota, @trace, @span, @parent, @reward, @epsilon);
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
                    cmd.ExecuteNonQuery();
                }

                BeforeAuditBatchCommitHook?.Invoke();
                tx.Commit();
            }
            catch
            {
                // Commit may be ambiguous; replaying is safer than losing audit records.
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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
                       completion_tokens, cost, latency_ms, session_id, routing_reason,
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
                       is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
                       cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
                       trace_id, span_id, parent_span_id, reward, epsilon_promoted_model
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
            FlushQueue();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, request_id, model, estimated_tokens, prompt_tokens,
                       completion_tokens, cost, latency_ms, session_id, routing_reason,
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
                       is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
                       cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
                       trace_id, span_id, parent_span_id, reward, epsilon_promoted_model
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
            FlushQueue();
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
                       success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from,
                       is_adopted, parallel_group_id, is_estimated, fusion_role, ttft_ms,
                       cached_input_tokens, cache_write_input_tokens, uncached_input_tokens, quota_limited,
                       trace_id, span_id, parent_span_id, reward, epsilon_promoted_model
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
    public (int Failures, int Total) GetFailureStats(DateTime from, DateTime to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            FlushQueue();
            using var cmd = _connection.CreateCommand();
            // 单条聚合：SUM(CASE...) 统计失败数，COUNT(*) 统计总数。
            // 替代 GetByTimeRange(int.MaxValue) 全量物化，O(1) 内存。
            cmd.CommandText = """
                SELECT COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0),
                       COUNT(*)
                FROM request_audit
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
            // 单条聚合查询，与 GetFailureStats 同模式：O(1) 内存，走 timestamp 索引。
            // from=DateTime.MinValue 时 timestamp >= '0001-...' 等价无下界（"全部"窗口）。
            using var cmd = _connection.CreateCommand();
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
                FROM request_audit
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
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM request_audit WHERE timestamp < @cutoff;";
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
            // 需计算 p95，SQLite AVG() 无法直接给出百分位。逐行拉取窗口内成功延迟，
            // C# 侧分组排序算 avg + p95。窗口（默认 60min）内模型 <50、样本有界，后台低频聚合可接受。
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT model, latency_ms
                FROM request_audit
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
            _connection.Dispose();
        }
        _signal.Dispose();
        _cts.Dispose();
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
                EpsilonPromotedModel: reader.IsDBNull(29) ? null : reader.GetString(29)));
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
