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
            if (models is not null)
            {
                for (int i = 0; i < models.Count; i++)
                {
                    var m = models[i];
                    string prefix = $"OptiRouter:Models:{i}:";
                    SetModel(Data, prefix, m);
                }
            }
        }
        catch
        {
            // 配置损坏时以空列表继续，不阻断启动
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SetModel(IDictionary<string, string?> data, string prefix, ModelEndpointOptions m)
    {
        data[$"{prefix}Name"] = m.Name;
        data[$"{prefix}BaseUrl"] = m.BaseUrl;
        data[$"{prefix}ApiKey"] = m.ApiKey;
        data[$"{prefix}Tier"] = m.Tier.ToString();
        data[$"{prefix}MaxContextTokens"] = m.MaxContextTokens.ToString();
        data[$"{prefix}InputPricePerMillion"] = m.InputPricePerMillion.ToString();
        data[$"{prefix}OutputPricePerMillion"] = m.OutputPricePerMillion.ToString();
        data[$"{prefix}TimeoutSeconds"] = m.TimeoutSeconds.ToString();
        data[$"{prefix}MaxRetries"] = m.MaxRetries.ToString();
        data[$"{prefix}Enabled"] = m.Enabled.ToString();
        // Tags 暂不映射为分段配置项（Dashboard UI 通过 ModelsConfigService API 读取完整 JSON）
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
