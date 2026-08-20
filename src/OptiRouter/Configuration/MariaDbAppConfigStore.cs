using System.Globalization;
using System.Text.Json;
using MySqlConnector;

namespace OptiRouter.Configuration;

/// <summary>
/// MariaDB/MySQL 版应用配置存储，与 <see cref="AppConfigDbStore"/>（SQLite 版）同构的
/// routing/budget 单文档 + 每模型一行存储，经 <see cref="AppConfigDbStore"/> 门面按
/// <c>ConfigDbConnectionString</c> 切换启用。所有方法内部以 _gate 串行化（与 SQLite 版一致）。
/// </summary>
/// <remarks>
/// 方言差异：列 <c>key</c> 为 MariaDB 保留字需反引号；<c>JSON_EXTRACT</c> 返回带引号的 JSON
/// 文本，比较前需 <c>JSON_UNQUOTE</c>；<c>IN</c> 子查询不支持 <c>LIMIT</c>，用派生表绕开。
/// </remarks>
internal sealed class MariaDbAppConfigStore : IDisposable
{
    private const string Table = "optirouter_app_config";
    private const string DocumentKey = "document";

    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _disposed;

    public MariaDbAppConfigStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // updated_at/ts 由写入方传 UTC ISO 字符串（毫秒精度），与 SQLite 版 strftime 格式一致。
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {Table} (
                scope      VARCHAR(64)  NOT NULL,
                `key`      VARCHAR(512) NOT NULL,
                value      LONGTEXT     NOT NULL,
                ord        INT          NOT NULL DEFAULT 0,
                updated_at VARCHAR(40)  NOT NULL,
                PRIMARY KEY (scope, `key`)
            );
            CREATE TABLE IF NOT EXISTS optirouter_config_change_history (
                id      BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
                ts      VARCHAR(40)  NOT NULL,
                actor   VARCHAR(255) NOT NULL,
                summary LONGTEXT     NOT NULL
            );
            CREATE TABLE IF NOT EXISTS optirouter_eval_batches (
                batch_id    VARCHAR(128) NOT NULL PRIMARY KEY,
                ts          VARCHAR(40)  NOT NULL,
                report_json LONGTEXT     NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>追加一条配置变更记录；超过 200 条时淘汰最旧。</summary>
    public void AppendConfigChange(string actor, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO optirouter_config_change_history (ts, actor, summary) VALUES (@ts, @a, @s);";
                cmd.Parameters.AddWithValue("@ts", NowTimestamp());
                cmd.Parameters.AddWithValue("@a", actor);
                cmd.Parameters.AddWithValue("@s", summary);
                cmd.ExecuteNonQuery();
            }
            using var prune = conn.CreateCommand();
            prune.CommandText = """
                DELETE FROM optirouter_config_change_history
                WHERE id <= (SELECT MAX(id) FROM optirouter_config_change_history) - 200;
                """;
            prune.ExecuteNonQuery();
        }
    }

    /// <summary>读取最近的配置变更记录（按时间倒序）。</summary>
    public IList<AppConfigDbStore.ConfigChangeEntry> LoadConfigChanges(int limit = 50)
    {
        if (limit <= 0) limit = 50;
        lock (_gate)
        {
            var result = new List<AppConfigDbStore.ConfigChangeEntry>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts, actor, summary FROM optirouter_config_change_history ORDER BY id DESC LIMIT @l;";
            cmd.Parameters.AddWithValue("@l", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AppConfigDbStore.ConfigChangeEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            return result;
        }
    }

    /// <summary>保存一份评测批次报告（JSON），并裁剪到最近 <paramref name="maxBatches"/> 批。</summary>
    public void SaveEvalBatch(string batchId, string timestamp, string reportJson, int maxBatches = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(reportJson);
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO optirouter_eval_batches (batch_id, ts, report_json) VALUES (@b, @t, @j)
                        ON DUPLICATE KEY UPDATE ts = VALUES(ts), report_json = VALUES(report_json);
                        """;
                    cmd.Parameters.AddWithValue("@b", batchId);
                    cmd.Parameters.AddWithValue("@t", timestamp ?? string.Empty);
                    cmd.Parameters.AddWithValue("@j", reportJson);
                    cmd.ExecuteNonQuery();
                }
                using (var prune = conn.CreateCommand())
                {
                    prune.Transaction = tx;
                    // MariaDB 的 IN 子查询不支持 LIMIT，包一层派生表。
                    prune.CommandText = """
                        DELETE FROM optirouter_eval_batches
                        WHERE batch_id NOT IN (
                            SELECT batch_id FROM (SELECT batch_id FROM optirouter_eval_batches ORDER BY ts DESC LIMIT @m) AS keep
                        );
                        """;
                    prune.Parameters.AddWithValue("@m", maxBatches);
                    prune.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>读取全部已持久化的评测批次（batchId, 时间戳, 报告 JSON），按时间倒序。</summary>
    public IList<(string BatchId, string Timestamp, string ReportJson)> LoadEvalBatches()
    {
        lock (_gate)
        {
            var result = new List<(string, string, string)>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT batch_id, ts, report_json FROM optirouter_eval_batches ORDER BY ts DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }
    }

    /// <summary>是否存在任何已持久化配置（决定是否执行首启迁移）。</summary>
    public bool HasData()
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {Table} LIMIT 1);";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
    }

    /// <summary>读取单文档 scope（routing/budget）的 JSON 文本；不存在返回 null。</summary>
    public string? LoadDocument(string scope)
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT value FROM {Table} WHERE scope = @s AND `key` = @k;";
            cmd.Parameters.AddWithValue("@s", scope);
            cmd.Parameters.AddWithValue("@k", DocumentKey);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>覆盖写入单文档 scope。原子（UPSERT）。</summary>
    public void SaveDocument(string scope, string json)
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {Table} (scope, `key`, value, ord, updated_at)
                VALUES (@s, @k, @v, 0, @ts)
                ON DUPLICATE KEY UPDATE
                    value = VALUES(value),
                    updated_at = VALUES(updated_at);
                """;
            cmd.Parameters.AddWithValue("@s", scope);
            cmd.Parameters.AddWithValue("@k", DocumentKey);
            cmd.Parameters.AddWithValue("@v", json);
            cmd.Parameters.AddWithValue("@ts", NowTimestamp());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>原子读取路由/预算文档及其内容版本。</summary>
    public (string? RoutingJson, string? BudgetJson, string Version) LoadRoutingBudgetSnapshot()
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            string? routing = LoadDocumentNoLock(conn, transaction: null, scope: AppConfigDbStore.RoutingScope);
            string? budget = LoadDocumentNoLock(conn, transaction: null, scope: AppConfigDbStore.BudgetScope);
            return (routing, budget, ComputeDocumentsVersion(routing, budget));
        }
    }

    /// <summary>
    /// 仅当当前路由/预算文档版本仍等于 <paramref name="expectedVersion"/> 时原子覆盖两份文档。
    /// </summary>
    public bool TrySaveRoutingBudgetDocuments(
        string expectedVersion,
        string routingJson,
        string budgetJson,
        out string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentNullException.ThrowIfNull(routingJson);
        ArgumentNullException.ThrowIfNull(budgetJson);

        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var transaction = conn.BeginTransaction();
            string? currentRouting = LoadDocumentNoLock(conn, transaction, AppConfigDbStore.RoutingScope);
            string? currentBudget = LoadDocumentNoLock(conn, transaction, AppConfigDbStore.BudgetScope);
            string currentVersion = ComputeDocumentsVersion(currentRouting, currentBudget);
            if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
            {
                version = currentVersion;
                transaction.Rollback();
                return false;
            }

            SaveDocumentNoLock(conn, transaction, AppConfigDbStore.RoutingScope, routingJson);
            SaveDocumentNoLock(conn, transaction, AppConfigDbStore.BudgetScope, budgetJson);
            transaction.Commit();
            version = ComputeDocumentsVersion(routingJson, budgetJson);
            return true;
        }
    }

    /// <remarks>调用方保证 conn 已打开；transaction 非空时命令挂到该事务。</remarks>
    private string? LoadDocumentNoLock(MySqlConnection conn, MySqlTransaction? transaction, string scope)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT value FROM {Table} WHERE scope = @s AND `key` = @k;";
        cmd.Parameters.AddWithValue("@s", scope);
        cmd.Parameters.AddWithValue("@k", DocumentKey);
        return cmd.ExecuteScalar() as string;
    }

    private void SaveDocumentNoLock(MySqlConnection conn, MySqlTransaction transaction, string scope, string json)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT INTO {Table} (scope, `key`, value, ord, updated_at)
            VALUES (@s, @k, @v, 0, @ts)
            ON DUPLICATE KEY UPDATE
                value = VALUES(value),
                updated_at = VALUES(updated_at);
            """;
        cmd.Parameters.AddWithValue("@s", scope);
        cmd.Parameters.AddWithValue("@k", DocumentKey);
        cmd.Parameters.AddWithValue("@v", json);
        cmd.Parameters.AddWithValue("@ts", NowTimestamp());
        cmd.ExecuteNonQuery();
    }

    private static string ComputeDocumentsVersion(string? routingJson, string? budgetJson)
    {
        routingJson ??= string.Empty;
        budgetJson ??= string.Empty;
        string content = $"{routingJson.Length}:{routingJson}{budgetJson.Length}:{budgetJson}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    /// <summary>读取原始模型列表（不展开 env: 引用），按 ord 排序。</summary>
    public IList<ModelEndpointOptions> LoadModelsRaw()
    {
        lock (_gate)
        {
            var list = new List<ModelEndpointOptions>();
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT value FROM {Table} WHERE scope = @s ORDER BY ord ASC;";
            cmd.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? json = reader.GetString(0);
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var model = JsonSerializer.Deserialize<ModelEndpointOptions>(json, AppConfigDbStore.ModelsFileJsonOptions);
                    if (model is not null)
                        list.Add(model);
                }
                catch (JsonException)
                {
                    // 单行损坏跳过，不阻断其余模型（与旧文件 tolerateErrors 语义一致）。
                }
            }
            return list;
        }
    }

    /// <summary>整体替换模型列表（给定顺序即 ord）。</summary>
    public void SaveModels(IEnumerable<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = $"DELETE FROM {Table} WHERE scope = @s;";
                    del.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
                    del.ExecuteNonQuery();
                }

                int ord = 0;
                foreach (var model in models)
                {
                    string key = AppConfigDbStore.ModelKey(model, ord);
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = $"""
                        INSERT INTO {Table} (scope, `key`, value, ord, updated_at)
                        VALUES (@s, @k, @v, @o, @ts);
                        """;
                    ins.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
                    ins.Parameters.AddWithValue("@k", key);
                    ins.Parameters.AddWithValue("@v", JsonSerializer.Serialize(model, AppConfigDbStore.ModelsFileJsonOptions));
                    ins.Parameters.AddWithValue("@o", ord++);
                    ins.Parameters.AddWithValue("@ts", NowTimestamp());
                    ins.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>新增或按名称/Id 更新单个模型；返回影响行数。</summary>
    public int UpsertModel(ModelEndpointOptions model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            string key = AppConfigDbStore.ModelKey(model, 0);
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                int rows;
                // SQLite 版用 INSERT..SELECT 计算 ord + ON CONFLICT；MariaDB 改为事务内先查后写。
                // 保留原 ord（存在时），否则追加到列表末尾（MAX(ord)+1）。
                long? existingOrd = null;
                using (var find = conn.CreateCommand())
                {
                    find.Transaction = tx;
                    find.CommandText = $"SELECT ord FROM {Table} WHERE scope = @s AND `key` = @k;";
                    find.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
                    find.Parameters.AddWithValue("@k", key);
                    var result = find.ExecuteScalar();
                    if (result is not null && result != DBNull.Value)
                        existingOrd = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }

                if (existingOrd.HasValue)
                {
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = $"UPDATE {Table} SET value = @v, updated_at = @ts WHERE scope = @s AND `key` = @k;";
                    upd.Parameters.AddWithValue("@v", JsonSerializer.Serialize(model, AppConfigDbStore.ModelsFileJsonOptions));
                    upd.Parameters.AddWithValue("@ts", NowTimestamp());
                    upd.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
                    upd.Parameters.AddWithValue("@k", key);
                    rows = upd.ExecuteNonQuery();
                }
                else
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = $"""
                        INSERT INTO {Table} (scope, `key`, value, ord, updated_at)
                        VALUES (@s, @k, @v, (SELECT COALESCE(MAX(ord), -1) + 1 FROM {Table} WHERE scope = @s), @ts);
                        """;
                    ins.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
                    ins.Parameters.AddWithValue("@k", key);
                    ins.Parameters.AddWithValue("@v", JsonSerializer.Serialize(model, AppConfigDbStore.ModelsFileJsonOptions));
                    ins.Parameters.AddWithValue("@ts", NowTimestamp());
                    rows = ins.ExecuteNonQuery();
                }

                tx.Commit();
                return rows;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>按名称或 Id 删除模型；返回是否删除。</summary>
    public bool DeleteModel(string nameOrId)
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // JSON_EXTRACT 返回带引号 JSON 文本，须 JSON_UNQUOTE 后比较（SQLite json_extract 直接返回裸值）。
            cmd.CommandText = $"""
                DELETE FROM {Table}
                WHERE scope = @s
                  AND (`key` = @k OR (JSON_UNQUOTE(JSON_EXTRACT(value, '$.id')) = @k AND JSON_UNQUOTE(JSON_EXTRACT(value, '$.name')) = ''));
                """;
            cmd.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
            cmd.Parameters.AddWithValue("@k", nameOrId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>读取给定模型的原始 ApiKey（用于保存时恢复 env: 字面量）。</summary>
    public string? GetRawApiKey(string nameOrId)
    {
        lock (_gate)
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT JSON_UNQUOTE(JSON_EXTRACT(value, '$.apiKey')) FROM {Table} WHERE scope = @s AND `key` = @k;";
            cmd.Parameters.AddWithValue("@s", AppConfigDbStore.ModelScope);
            cmd.Parameters.AddWithValue("@k", nameOrId);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string NowTimestamp()
        => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
