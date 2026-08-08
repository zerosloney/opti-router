using System.Collections.ObjectModel;

namespace OptiRouter.Configuration;

/// <summary>
/// 单个模型端点配置。
/// </summary>
public sealed class ModelEndpointOptions
{
    /// <summary>
    /// 模型唯一标识，如 "gpt-4o"、"deepseek-chat"。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI 兼容 API base url，如 "https://api.openai.com/v1"。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// API Key。应从环境变量或 user-secrets 注入，不要在 appsettings.json 中明文存放。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// 能力分档。
    /// </summary>
    public ModelTier Tier { get; set; } = ModelTier.Medium;

    /// <summary>
    /// 最大上下文窗口（token 数）。
    /// </summary>
    public int MaxContextTokens { get; set; } = 8192;

    /// <summary>
    /// 输入 token 价格（USD / 百万 token）。
    /// </summary>
    public decimal InputPricePerMillion { get; set; }

    /// <summary>
    /// 输出 token 价格（USD / 百万 token）。
    /// </summary>
    public decimal OutputPricePerMillion { get; set; }

    /// <summary>
    /// 单次请求超时秒数。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 单模型内部重试次数。降级路由由 RouterEngine 负责，这里只控制同模型的重试。
    /// </summary>
    public int MaxRetries { get; set; } = 0;

    /// <summary>
    /// 是否启用该模型。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 可选能力标签，如 ["vision","tool-use"]。
    /// </summary>
    public IList<string> Tags { get; } = new Collection<string>();

    /// <summary>
    /// 模型在各维度的能力评分（0.0 至 1.0），如 "coding": 0.95, "reasoning": 0.90。
    /// </summary>
    public IDictionary<string, double> Capabilities { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}
