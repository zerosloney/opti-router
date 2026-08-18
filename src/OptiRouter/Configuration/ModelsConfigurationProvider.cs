using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OptiRouter.Configuration;

/// <summary>
/// 将 models-config.json 文件映射为 IConfiguration 中的 "OptiRouter:Models" 节点。
/// ASP.NET Core 配置系统会在启动时从此读取，后续热更新由 <see cref="ModelsConfigService"/> 调用
/// <see cref="IConfigurationRoot.Reload()"/> 触发。
/// </summary>
public sealed class ModelsJsonConfigurationProvider : ConfigurationProvider
{
    private readonly string _filePath;

    /// <summary>
    /// 用 models-config.json 文件初始化提供者。
    /// </summary>
    /// <param name="filePath">models-config.json 路径。</param>
    public ModelsJsonConfigurationProvider(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// 从 models-config.json 加载模型到配置字典。
    /// </summary>
    public override void Load()
    {
        if (!File.Exists(_filePath))
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            var models = JsonSerializer.Deserialize<List<ModelEndpointOptions>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });

            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            // IConfiguration 的数组节点由多个 provider 合并，空数组无法为低优先级
            // appsettings 条目写入删除墓碑。RouterOptions 的配置阶段会从
            // ModelsConfigService 重新替换 Models，因此空/缩短数组不会复活旧模型。
            if (models is not null && models.Count > 0)
            {
                for (int i = 0; i < models.Count; i++)
                {
                    var m = models[i];
                    string prefix = $"OptiRouter:Models:{i}:";
                    SetModel(Data, prefix, m);
                }
            }
        }
        catch (Exception ex)
        {
            // 配置损坏时以空列表继续，不阻断启动；但必须留下原因——
            // 否则模型静默消失，全部请求无候选且无任何诊断线索。
            // 此处处于日志系统建立前的引导阶段，Console.Error 由宿主重定向到 stderr。
            Console.Error.WriteLine(
                $"[ModelsJsonConfigurationProvider] models-config.json ('{_filePath}') failed to load: {ex.Message}. " +
                "Continuing with an EMPTY model list; all models are unavailable until the file is fixed.");
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SetModel(IDictionary<string, string?> data, string prefix, ModelEndpointOptions m)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        data[$"{prefix}Name"] = m.Name;
        data[$"{prefix}Id"] = m.Id;
        data[$"{prefix}BaseUrl"] = m.BaseUrl;
        data[$"{prefix}ApiKey"] = m.ApiKey;
        data[$"{prefix}Provider"] = m.Provider;
        data[$"{prefix}Family"] = m.Family;
        data[$"{prefix}Tier"] = m.Tier.ToString();
        data[$"{prefix}MaxContextTokens"] = m.MaxContextTokens.ToString(invariant);
        data[$"{prefix}InputPricePerMillion"] = m.InputPricePerMillion.ToString(invariant);
        data[$"{prefix}CachedInputPricePerMillion"] = m.CachedInputPricePerMillion?.ToString(invariant);
        data[$"{prefix}CacheWriteInputPricePerMillion"] = m.CacheWriteInputPricePerMillion?.ToString(invariant);
        data[$"{prefix}OutputPricePerMillion"] = m.OutputPricePerMillion.ToString(invariant);
        data[$"{prefix}TimeoutSeconds"] = m.TimeoutSeconds.ToString(invariant);
        data[$"{prefix}MaxRetries"] = m.MaxRetries.ToString(invariant);
        data[$"{prefix}Enabled"] = m.Enabled.ToString();
        data[$"{prefix}IsLocalOrPrivate"] = m.IsLocalOrPrivate.ToString();
        for (int i = 0; i < m.Tags.Count; i++)
            data[$"{prefix}Tags:{i}"] = m.Tags[i];
        if (m.Capabilities is not null)
        {
            foreach (var kvp in m.Capabilities)
            {
                data[$"{prefix}Capabilities:{kvp.Key}"] = kvp.Value.ToString(invariant);
            }
        }
    }
}

/// <summary>
/// 配合 <see cref="ModelsJsonConfigurationProvider"/> 的配置源。
/// </summary>
public sealed class ModelsJsonConfigurationSource : IConfigurationSource
{
    /// <summary>
    /// models-config.json 文件路径。
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// 创建配置提供者。
    /// </summary>
    /// <param name="builder">配置构建器。</param>
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new ModelsJsonConfigurationProvider(FilePath);
    }
}
