using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OptiRouter.Configuration;

/// <summary>
/// 将应用配置库（routing/budget 文档 + 模型列表，SQLite 或 MariaDB 后端）映射为
/// IConfiguration 的 "OptiRouter:Routing" / "OptiRouter:Budget" / "OptiRouter:Models" 节点。
/// 配置写入由 <see cref="AppConfigDbStore"/> 完成，随后 IConfigurationRoot.Reload()
/// 重新触发本提供者的 <see cref="Load"/>，IOptionsMonitor 热生效。
/// </summary>
public sealed class DbAppConfigProvider : ConfigurationProvider
{
    private readonly string _dbPath;
    private readonly string? _connectionString;

    public DbAppConfigProvider(string dbPath, string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        _dbPath = dbPath;
        _connectionString = connectionString;
    }

    public override void Load()
    {
        try
        {
            using var store = new AppConfigDbStore(_dbPath, _connectionString);
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            AddDocument(store, AppConfigDbStore.RoutingScope, "OptiRouter:Routing", data);
            AddDocument(store, AppConfigDbStore.BudgetScope, "OptiRouter:Budget", data);

            var models = store.LoadModelsRaw();
            for (int i = 0; i < models.Count; i++)
            {
                Flatten(
                    JsonSerializer.SerializeToElement(models[i], AppConfigDbStore.ModelsFileJsonOptions),
                    $"OptiRouter:Models:{i}",
                    data);
            }

            Data = data;
        }
        catch (Exception ex)
        {
            // 运行期 Reload 也会进这里（页面保存配置 → IConfigurationRoot.Reload）。
            // DB 瞬时不可用时必须保留上一次成功加载的 Data：清空会让 IOptionsMonitor 立即
            // 回落默认值、模型列表清空，并连带 OnChange → Retain(空) 清掉学习状态。
            // 仅首次加载（尚无旧数据）才以空字典继续，不阻断启动。
            Console.Error.WriteLine(
                $"[DbAppConfigProvider] config db '{_dbPath}' failed to load: {ex.Message}. " +
                (Data.Count > 0
                    ? "Keeping previously loaded config values."
                    : "Continuing with default config; routing/model values from the database are unavailable."));
            if (Data.Count == 0)
                Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AddDocument(
        AppConfigDbStore store,
        string scope,
        string prefix,
        IDictionary<string, string?> data)
    {
        if (store.LoadDocument(scope) is not { } json || string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            Flatten(JsonDocument.Parse(json).RootElement, prefix, data);
        }
        catch (JsonException)
        {
            // 单文档损坏：跳过该段，保留其余（启动后由页面重新保存修复）。
        }
    }

    /// <summary>通用 JSON → 扁平配置键（对象/数组递归展开，标量转字符串，null 跳过=回退绑定默认值）。</summary>
    private static void Flatten(JsonElement el, string prefix, IDictionary<string, string?> data)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    Flatten(prop.Value, $"{prefix}:{prop.Name}", data);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in el.EnumerateArray())
                    Flatten(item, $"{prefix}:{i++}", data);
                break;
            case JsonValueKind.String:
                data[prefix] = el.GetString();
                break;
            case JsonValueKind.Number:
                data[prefix] = el.GetRawText();
                break;
            case JsonValueKind.True:
                data[prefix] = "true";
                break;
            case JsonValueKind.False:
                data[prefix] = "false";
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
        }
    }
}

/// <summary>
/// 配合 <see cref="DbAppConfigProvider"/> 的配置源。
/// </summary>
public sealed class DbAppConfigSource : IConfigurationSource
{
    /// <summary>SQLite 配置库路径。</summary>
    public string DbPath { get; init; } = string.Empty;

    /// <summary>MariaDB 连接串；非空时配置库走 MariaDB 后端（<c>OptiRouter:ConfigDbConnectionString</c>）。</summary>
    public string? ConnectionString { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new DbAppConfigProvider(DbPath, ConnectionString);
    }
}
