using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OptiRouter.Routing;

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

    /// <summary>
    /// 记录原始 env: 字面量（模型名 → ApiKey 字面量），用于序列化时恢复环境变量引用。
    /// 仅记录以 "env:" 开头的 ApiKey，每次加载时重建。
    /// </summary>
    private readonly Dictionary<string, string> _rawApiKeys = new(StringComparer.Ordinal);

    // Test-only seam: lets the atomic replacement failure path be exercised
    // without relying on platform-specific file permission behavior.
    internal Action<string, string>? AtomicReplaceHook { get; set; }

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

        // 文件系统监控，变更时触发配置热重载。
        // NotifyFilter 含 FileName/CreationTime 以捕获部分编辑器的 delete-and-recreate 保存
        // （仅监 LastWrite|Size 会漏检 rename 覆盖）。Changed/Created/Renamed 均触发重载。
        string? name = Path.GetFileName(_filePath);
        if (dir is not null && name is not null)
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
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
            return LoadModelsNoLock(tolerateErrors: true);
        }
    }

    /// <summary>
    /// 将模型列表写入文件并触发配置热重载。
    /// </summary>
    public void SaveModels(IEnumerable<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        lock (_gate)
        {
            var snapshot = models.ToList();
            WriteModelsToFile(snapshot);
            _logger.LogInformation("Saved {Count} models to models-config.json", snapshot.Count);
        }

        ReloadConfiguration();
    }

    /// <summary>
    /// 更新单个模型（按名称匹配），不存在则添加。
    /// </summary>
    public void UpsertModel(ModelEndpointOptions model)
    {
        // 落盘前校验数值边界，与启动校验（RouterOptionsValidator）一致，避免坏配置导致重启 ValidateOnStart 失败。
        string? error = RouterOptionsValidator.ValidateModel(model);
        if (error is not null)
            throw new ArgumentException(error, nameof(model));

        WarnUnknownTags(model);

        lock (_gate)
        {
            var models = LoadModelsNoLock(tolerateErrors: false);
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

            WriteModelsToFile(models);
            _logger.LogInformation("Saved {Count} models to models-config.json", models.Count);
        }

        ReloadConfiguration();
    }

    /// <summary>
    /// 删除指定名称的模型。
    /// </summary>
    public bool DeleteModel(string name)
    {
        bool deleted;
        lock (_gate)
        {
            var models = LoadModelsNoLock(tolerateErrors: false);
            int removed = models.RemoveAll(m =>
                string.Equals(m.Name, name, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            // 清理 _rawApiKeys 中已删模型的条目（防字典随时间膨胀）
            _rawApiKeys.Remove(name);

            WriteModelsToFile(models);
            _logger.LogInformation("Saved {Count} models to models-config.json", models.Count);
            deleted = true;
        }

        if (deleted)
            ReloadConfiguration();
        return deleted;
    }

    /// <summary>
    /// 获取配置文件路径（供 Dashboard UI 显示）。
    /// </summary>
    public string ConfigFilePath => _filePath;

    private void WriteModelsToFile(IEnumerable<ModelEndpointOptions> models)
    {
        // 恢复 env: 引用：对每个模型，若原始是 env:X 且当前值等于环境变量值，恢复为 env:X
        var modelsList = models.ToList();
        var originalApiKeys = new Dictionary<string, string?>(modelsList.Count);

        for (int i = 0; i < modelsList.Count; i++)
        {
            var model = modelsList[i];
            originalApiKeys[model.Name] = model.ApiKey;

            // 若原始是 env: 引用
            if (_rawApiKeys.TryGetValue(model.Name, out string? rawLiteral) &&
                rawLiteral.StartsWith("env:", StringComparison.Ordinal))
            {
                string envVarName = rawLiteral.Substring(4);
                string currentEnvValue = Environment.GetEnvironmentVariable(envVarName) ?? "";

                // 若当前 ApiKey 恰好等于环境变量值（说明用户没改 key），恢复为 env: 字面量
                if (model.ApiKey == currentEnvValue)
                {
                    model.ApiKey = rawLiteral;
                }
                // 否则：用户传入了新明文 key（≠ 环境变量当前值）或传入值本身就是 env: 开头，照写
            }
        }

        string json = JsonSerializer.Serialize(modelsList, JsonOptions);
        string targetPath = Path.GetFullPath(_filePath);
        string directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("models-config.json path has no parent directory");
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (AtomicReplaceHook is { } replace)
            {
                replace(tempPath, targetPath);
            }
            else
            {
                int attempts = 0;
                while (true)
                {
                    try
                    {
                        if (File.Exists(targetPath))
                            File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                        else
                            File.Move(tempPath, targetPath);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        attempts++;
                        if (attempts >= 10)
                            throw;
                        Thread.Sleep(10 * attempts);
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        // 序列化完成后恢复原始 ApiKey 值（不永久修改调用方对象）
        for (int i = 0; i < modelsList.Count; i++)
        {
            var model = modelsList[i];
            if (originalApiKeys.TryGetValue(model.Name, out string? originalKey))
            {
                model.ApiKey = originalKey;
            }
        }
    }

    private List<ModelEndpointOptions> LoadModelsNoLock(bool tolerateErrors)
    {
        try
        {
            string json = File.ReadAllText(_filePath);
            var models = JsonSerializer.Deserialize<List<ModelEndpointOptions>>(json, JsonOptions);
            var result = models ?? new List<ModelEndpointOptions>();

            // 重建原始 env: 字面量字典（每次加载时清空重建）
            _rawApiKeys.Clear();

            // 处理 env:VAR 环境变量引用语法
            foreach (var model in result)
            {
                if (!string.IsNullOrWhiteSpace(model.ApiKey) && model.ApiKey.StartsWith("env:", StringComparison.Ordinal))
                {
                    string envVarName = model.ApiKey.Substring(4);
                    string envValue = Environment.GetEnvironmentVariable(envVarName) ?? "";

                    // 记录原始字面量，供序列化时恢复
                    _rawApiKeys[model.Name] = model.ApiKey;

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

            return result;
        }
        catch (Exception ex)
        {
            if (!tolerateErrors)
                throw;

            _logger.LogError(ex, "Failed to load models-config.json, returning empty list");
            return new List<ModelEndpointOptions>();
        }
    }

    private void ReloadConfiguration()
    {
        // Reload synchronously fans out IOptionsMonitor callbacks. Keep it
        // outside _gate so callbacks can safely call back into this service.
        _configRoot.Reload();
        _logger.LogInformation("Configuration reloaded after models-config.json write");
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

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖：忽略短时间内的重复事件（Windows 文件系统有时会触发多次）
        Thread.Sleep(50);
        try
        {
            // Reload 不持 _gate：Reload 同步扇出所有 IOptionsMonitor.OnChange 回调，
            // 持锁会让慢回调阻塞 watcher 线程并阻塞 SaveModels/LoadModels。Reload 本身线程安全。
            _configRoot.Reload();
            _logger.LogInformation("models-config.json changed externally ({ChangeType}), configuration reloaded", e.ChangeType);
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
        // _watcher 在 dir/name 不可解析时为 null（见构造器），Dispose 需防御。
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
