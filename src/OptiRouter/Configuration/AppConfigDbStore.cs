using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace OptiRouter.Configuration;

/// <summary>
/// SQLite 应用配置存储：替代 appsettings.json 的 Routing/Budget 段与 models-config.json。
/// <list type="bullet">
/// <item><c>routing</c> / <c>budget</c>：单文档（key=<c>document</c>，值为 JSON 文本）；</item>
/// <item><c>model</c>：每模型一行（key=模型名或 Id，<c>ord</c> 保序，值为模型 JSON）。</item>
/// </list>
/// 页面写入经 <see cref="ModelsConfigService"/>（模型）与 DashboardHandler（路由/预算）落到本存储，
/// 随后触发 <see cref="Microsoft.Extensions.Configuration.IConfigurationRoot.Reload"/> 热生效。
/// </summary>
public sealed class AppConfigDbStore : IDisposable
{
    public const string RoutingScope = "routing";
    public const string BudgetScope = "budget";
    public const string ModelScope = "model";
    private const string DocumentKey = "document";

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>模型列表读写用，保持与旧 models-config.json 一致的序列化契约（缩进、camelCase、枚举字符串）。</summary>
    internal static readonly JsonSerializerOptions ModelsFileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AppConfigDbStore(string dbPath)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        string fullPath = Path.GetFullPath(dbPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={fullPath};Default Timeout=15;Pooling=False");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_config (
                scope      TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      TEXT NOT NULL,
                ord        INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                PRIMARY KEY (scope, key)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>是否存在任何已持久化配置（决定是否执行首启迁移）。</summary>
    public bool HasData()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM app_config LIMIT 1);";
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
    }

    /// <summary>读取单文档 scope（routing/budget）的 JSON 文本；不存在返回 null。</summary>
    public string? LoadDocument(string scope)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_config WHERE scope = $s AND key = $k;";
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$k", DocumentKey);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>覆盖写入单文档 scope。原子（UPSERT）。</summary>
    public void SaveDocument(string scope, string json)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_config (scope, key, value, ord, updated_at)
                VALUES ($s, $k, $v, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT (scope, key) DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$k", DocumentKey);
            cmd.Parameters.AddWithValue("$v", json);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>读取原始模型列表（不展开 env: 引用），按 ord 排序。</summary>
    public IList<ModelEndpointOptions> LoadModelsRaw()
    {
        lock (_gate)
        {
            var list = new List<ModelEndpointOptions>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_config WHERE scope = $s ORDER BY ord ASC;";
            cmd.Parameters.AddWithValue("$s", ModelScope);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? json = reader.GetString(0);
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var model = JsonSerializer.Deserialize<ModelEndpointOptions>(json, ModelsFileJsonOptions);
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
            using var tx = _connection.BeginTransaction();
            try
            {
                using (var del = _connection.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM app_config WHERE scope = $s;";
                    del.Parameters.AddWithValue("$s", ModelScope);
                    del.ExecuteNonQuery();
                }

                int ord = 0;
                foreach (var model in models)
                {
                    string key = ModelKey(model, ord);
                    using var ins = _connection.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO app_config (scope, key, value, ord, updated_at)
                        VALUES ($s, $k, $v, $o, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                        ON CONFLICT (scope, key) DO UPDATE SET
                            value = excluded.value,
                            ord = excluded.ord,
                            updated_at = excluded.updated_at;
                        """;
                    ins.Parameters.AddWithValue("$s", ModelScope);
                    ins.Parameters.AddWithValue("$k", key);
                    ins.Parameters.AddWithValue("$v", JsonSerializer.Serialize(model, ModelsFileJsonOptions));
                    ins.Parameters.AddWithValue("$o", ord++);
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
            string key = ModelKey(model, 0);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_config (scope, key, value, ord, updated_at)
                VALUES ($s, $k, $v, COALESCE((SELECT ord FROM app_config WHERE scope = $s AND key = $k),
                                             (SELECT COALESCE(MAX(ord), -1) + 1 FROM app_config WHERE scope = $s)),
                        strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT (scope, key) DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$s", ModelScope);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(model, ModelsFileJsonOptions));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>按名称或 Id 删除模型；返回是否删除。</summary>
    public bool DeleteModel(string nameOrId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM app_config
                WHERE scope = $s
                  AND (key = $k OR (json_extract(value, '$.id') = $k AND json_extract(value, '$.name') = ''));
                """;
            cmd.Parameters.AddWithValue("$s", ModelScope);
            cmd.Parameters.AddWithValue("$k", nameOrId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>读取给定模型的原始 ApiKey（用于保存时恢复 env: 字面量）。</summary>
    public string? GetRawApiKey(string nameOrId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT json_extract(value, '$.apiKey') FROM app_config WHERE scope = $s AND key = $k;";
            cmd.Parameters.AddWithValue("$s", ModelScope);
            cmd.Parameters.AddWithValue("$k", nameOrId);
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>模型行主键：Name 非空用 Name，否则 Id，否则用占位（防重复碰撞追加序号由归一化处理）。</summary>
    private static string ModelKey(ModelEndpointOptions model, int ord)
    {
        if (!string.IsNullOrWhiteSpace(model.Name))
            return model.Name;
        if (!string.IsNullOrWhiteSpace(model.Id))
            return model.Id;
        return "unnamed-" + ord.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
