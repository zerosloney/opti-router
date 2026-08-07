using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Configuration;

/// <summary>
/// 模型配置文件的读写与热更新服务。
/// 负责从 <c>models-config.json</c> 加载模型列表，支持 Dashboard 写入，
/// 并在文件变更时触发 <see cref="IConfigurationRoot.Reload()"/> 通知所有 IOptionsMonitor 实现。
/// </summary>
public sealed class ModelsConfigService : IDisposable
{
    private readonly string _filePath;
    private readonly IConfigurationRoot _configRoot;
    private readonly ILogger<ModelsConfigService> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 用模型配置路径、配置根和日志初始化服务。
    /// </summary>
    /// <param name="filePath">models-config.json 路径。</param>
    /// <param name="configRoot">IConfigurationRoot，用于触发重载。</param>
    /// <param name="logger">日志。</param>
    public ModelsConfigService(
        string filePath,
        IConfigurationRoot configRoot,
        ILogger<ModelsConfigService> logger)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(configRoot);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _configRoot = configRoot;
        _logger = logger;

        // 确保文件存在（写入默认空配置）
        string? dir = Path.GetDirectoryName(_filePath);
        if (!File.Exists(_filePath))
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            WriteModelsToFile(Enumerable.Empty<ModelEndpointOptions>());
            _logger.LogInformation("Created models-config.json at {Path}", _filePath);
        }

        // 文件系统监控，变更时触发配置热重载
        string? name = Path.GetFileName(_filePath);
        if (dir is not null && name is not null)
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        else
        {
            _watcher = null!;
        }
    }

    /// <summary>
    /// 从文件加载当前模型列表。
    /// </summary>
    public IList<ModelEndpointOptions> LoadModels()
    {
        lock (_gate)
        {
            try
            {
                string json = File.ReadAllText(_filePath);
                var models = JsonSerializer.Deserialize<List<ModelEndpointOptions>>(json, JsonOptions);
                return models ?? new List<ModelEndpointOptions>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load models-config.json, returning empty list");
                return new List<ModelEndpointOptions>();
            }
        }
    }

    /// <summary>
    /// 将模型列表写入文件并触发配置热重载。
    /// </summary>
    public void SaveModels(IEnumerable<ModelEndpointOptions> models)
    {
        lock (_gate)
        {
            WriteModelsToFile(models);
            _logger.LogInformation("Saved {Count} models to models-config.json", models.Count());

            // 触发 ASP.NET Core 配置重载 → IOptionsMonitor.OnChange 派发 → ModelClientProvider 热更新
            _configRoot.Reload();
            _logger.LogInformation("Configuration reloaded after models-config.json write");
        }
    }

    /// <summary>
    /// 更新单个模型（按名称匹配），不存在则添加。
    /// </summary>
    public void UpsertModel(ModelEndpointOptions model)
    {
        var models = LoadModels().ToList();
        var existing = models.FirstOrDefault(m =>
            string.Equals(m.Name, model.Name, StringComparison.Ordinal));
        if (existing is not null)
        {
            // 保留 ApiKey 不被空字符串覆盖
            if (string.IsNullOrEmpty(model.ApiKey) && !string.IsNullOrEmpty(existing.ApiKey))
                model.ApiKey = existing.ApiKey;

            int idx = models.IndexOf(existing);
            models[idx] = model;
        }
        else
        {
            models.Add(model);
        }
        SaveModels(models);
    }

    /// <summary>
    /// 删除指定名称的模型。
    /// </summary>
    public bool DeleteModel(string name)
    {
        var models = LoadModels().ToList();
        int removed = models.RemoveAll(m =>
            string.Equals(m.Name, name, StringComparison.Ordinal));
        if (removed > 0)
        {
            SaveModels(models);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 获取配置文件路径（供 Dashboard UI 显示）。
    /// </summary>
    public string ConfigFilePath => _filePath;

    private void WriteModelsToFile(IEnumerable<ModelEndpointOptions> models)
    {
        string json = JsonSerializer.Serialize(models.ToList(), JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖：忽略短时间内的重复事件（Windows 文件系统有时会触发多次）
        Thread.Sleep(50);
        try
        {
            lock (_gate)
            {
                _configRoot.Reload();
                _logger.LogInformation("models-config.json changed externally, configuration reloaded");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration after external file change");
        }
    }

    /// <summary>
    /// 释放文件系统监控器。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
