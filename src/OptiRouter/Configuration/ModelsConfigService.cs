using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OptiRouter.Routing;

namespace OptiRouter.Configuration;

/// <summary>
/// 模型配置的读写与热更新服务。
/// 负责从 SQLite 配置库（<see cref="AppConfigDbStore"/>）加载模型列表，支持 Dashboard 写入，
/// 并在写入后触发 <see cref="IConfigurationRoot.Reload()"/> 通知所有 IOptionsMonitor 实现。
/// </summary>
public sealed class ModelsConfigService
{
    private readonly AppConfigDbStore _store;
    private readonly IConfigurationRoot _configRoot;
    private readonly ILogger<ModelsConfigService> _logger;

    /// <summary>
    /// 记录原始 env: 字面量（模型名 → ApiKey 字面量），用于保存时恢复环境变量引用。
    /// 仅记录以 "env:" 开头的 ApiKey，每次加载时重建。
    /// </summary>
    private readonly Dictionary<string, string> _rawApiKeys = new(StringComparer.Ordinal);

    public ModelsConfigService(
        AppConfigDbStore store,
        IConfigurationRoot configRoot,
        ILogger<ModelsConfigService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configRoot);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _configRoot = configRoot;
        _logger = logger;
    }

    /// <summary>
    /// 从配置库加载当前模型列表（env:VAR ApiKey 展开为环境变量值）。
    /// </summary>
    public IList<ModelEndpointOptions> LoadModels()
    {
        var models = _store.LoadModelsRaw();
        _rawApiKeys.Clear();

        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.ApiKey) && model.ApiKey.StartsWith("env:", StringComparison.Ordinal))
            {
                string envVarName = model.ApiKey.Substring(4);
                string envValue = Environment.GetEnvironmentVariable(envVarName) ?? "";

                _rawApiKeys[KeyOf(model)] = model.ApiKey;

                if (string.IsNullOrEmpty(envValue))
                {
                    _logger.LogWarning(
                        "模型 {ModelName} 的 ApiKey 引用环境变量 {EnvVarName} 不存在或为空，该模型 ApiKey 视为空",
                        model.Name, envVarName);
                    model.ApiKey = "";
                }
                else
                {
                    model.ApiKey = envValue;
                }
            }
        }

        return models;
    }

    /// <summary>
    /// 将模型列表写入配置库并触发配置热重载。
    /// </summary>
    public void SaveModels(IEnumerable<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        lock (_rawApiKeys)
        {
            var snapshot = models.ToList();
            RestoreEnvLiterals(snapshot);
            _store.SaveModels(snapshot);
            _logger.LogInformation("Saved {Count} models to config database", snapshot.Count);
        }

        ReloadConfiguration();
    }

    /// <summary>
    /// 更新单个模型（按名称匹配），不存在则添加。
    /// </summary>
    public void UpsertModel(ModelEndpointOptions model)
    {
        // 落库前校验数值边界，与启动校验（RouterOptionsValidator）一致，避免坏配置导致重启 ValidateOnStart 失败。
        string? error = RouterOptionsValidator.ValidateModel(model);
        if (error is not null)
            throw new ArgumentException(error, nameof(model));

        WarnUnknownTags(model);

        lock (_rawApiKeys)
        {
            var existing = _store.LoadModelsRaw().FirstOrDefault(m =>
                string.Equals(m.Name, model.Name, StringComparison.Ordinal)
                || (string.IsNullOrWhiteSpace(m.Name) && !string.IsNullOrWhiteSpace(model.Name)
                    && string.Equals(m.Id, model.Id, StringComparison.Ordinal)));

            if (existing is not null)
            {
                // 保留 ApiKey 不被空字符串覆盖
                if (string.IsNullOrEmpty(model.ApiKey) && !string.IsNullOrEmpty(existing.ApiKey))
                    model.ApiKey = existing.ApiKey;

                // 恢复 env: 字面量：原值 env:X 且新值=环境变量当前值 → 写回字面量
                string? raw = _store.GetRawApiKey(KeyOf(existing));
                if (raw is { } rawLiteral
                    && rawLiteral.StartsWith("env:", StringComparison.Ordinal)
                    && string.Equals(model.ApiKey, Environment.GetEnvironmentVariable(rawLiteral.Substring(4)) ?? "", StringComparison.Ordinal))
                {
                    model.ApiKey = rawLiteral;
                }
            }

            _store.UpsertModel(model);
            _logger.LogInformation("Saved model {ModelName} to config database", model.Name);
        }

        ReloadConfiguration();
    }

    /// <summary>
    /// 删除指定名称的模型。
    /// </summary>
    public bool DeleteModel(string name)
    {
        bool deleted;
        lock (_rawApiKeys)
        {
            deleted = _store.DeleteModel(name);
            if (deleted)
            {
                _rawApiKeys.Remove(name);
                _logger.LogInformation("Deleted model {ModelName} from config database", name);
            }
        }

        if (deleted)
            ReloadConfiguration();
        return deleted;
    }

    /// <summary>
    /// 整体保存前恢复 env: 字面量（与 Upsert 语义一致：原值 env:X 且新值=环境变量值 → 写回字面量）。
    /// </summary>
    private void RestoreEnvLiterals(IEnumerable<ModelEndpointOptions> models)
    {
        var byKey = new Dictionary<string, ModelEndpointOptions>(StringComparer.Ordinal);
        foreach (var model in models)
            byKey[KeyOf(model)] = model;
        foreach (var (key, literal) in _rawApiKeys)
        {
            if (!literal.StartsWith("env:", StringComparison.Ordinal))
                continue;
            if (!byKey.TryGetValue(key, out var model))
                continue;

            string envValue = Environment.GetEnvironmentVariable(literal.Substring(4)) ?? "";
            if (string.Equals(model.ApiKey, envValue, StringComparison.Ordinal))
                model.ApiKey = literal;
        }
    }

    private static string KeyOf(ModelEndpointOptions model)
        => !string.IsNullOrWhiteSpace(model.Name) ? model.Name : model.Id;

    private void ReloadConfiguration()
    {
        // Reload 同步扇出 IOptionsMonitor 回调；不持锁以便回调可安全回调用本服务。
        _configRoot.Reload();
        _logger.LogInformation("Configuration reloaded after config database write");
    }

    /// <summary>
    /// Tags 软校验：未识别 tag 仅 warning，不阻断写入（与启动校验语义一致）。
    /// </summary>
    private void WarnUnknownTags(ModelEndpointOptions model)
    {
        if (model.Tags is null || model.Tags.Count == 0) return;
        var unknown = model.Tags
            .Where(t => !ModelCapabilities.KnownTags.Contains(t))
            .ToList();
        if (unknown.Count > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "模型 {Name} 含未识别的 Tags: {Unknown}。已知标签: {Known}。" +
                "若为拼写错误，CapabilityFilter 将无法匹配；自定义 tag 不影响其他策略。",
                model.Name, string.Join(", ", unknown), string.Join(", ", ModelCapabilities.KnownTags));
        }
    }
}
