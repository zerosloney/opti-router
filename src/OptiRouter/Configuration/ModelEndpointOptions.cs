using System.Collections.ObjectModel;

namespace OptiRouter.Configuration;

/// <summary>
/// 单个模型端点配置。
/// </summary>
public sealed class ModelEndpointOptions
{
    private IList<string> _tags = new Collection<string>();

    /// <summary>
    /// 模型唯一标识（路由名），如 "gpt-4o"、"deepseek/deepseek-chat"。
    /// 客户端请求 <c>model</c> 字段与 /v1/models 的 <c>id</c> 均指此名。
    /// 留空且配置了 <see cref="Id"/> 时，启动归一化为 "{供应商}/{Id}"（同供应商同模型重复时追加序号）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 上游真实模型 id，即实际发往供应商 API 的 <c>model</c> 值，如 "deepseek-chat"、"gpt-4o"。
    /// 留空时回退 <see cref="Name"/>（向后兼容）。可在多家供应商配置相同 Id（同一模型多供应商/多 Key）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 发往上游的真实模型 id：<see cref="Id"/> 留空时回退 <see cref="Name"/>。
    /// </summary>
    public string UpstreamModelId => string.IsNullOrWhiteSpace(Id) ? Name : Id;

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
    /// Provider identifier used only for soft routing diversity. Empty means unknown;
    /// custom values are supported.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Model family identifier used only for soft routing diversity. Empty means unknown;
    /// custom values are supported.
    /// </summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>
    /// Cached-input token price (USD / million tokens). Null falls back to
    /// <see cref="InputPricePerMillion"/>.
    /// </summary>
    public decimal? CachedInputPricePerMillion { get; set; }

    /// <summary>
    /// Cache-write input token price (USD / million tokens). Null falls back to
    /// <see cref="InputPricePerMillion"/>.
    /// </summary>
    public decimal? CacheWriteInputPricePerMillion { get; set; }

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
    /// 是否为本地/私有化部署节点（数据不出域标示）。
    /// </summary>
    public bool IsLocalOrPrivate { get; set; } = false;

    /// <summary>
    /// 可选能力标签，如 ["vision","tool-use"]。
    /// </summary>
    public IList<string> Tags
    {
        get => _tags;
        set => _tags = value ?? new Collection<string>();
    }

    /// <summary>
    /// 模型在各维度的能力评分（0.0 至 1.0），如 "coding": 0.95, "reasoning": 0.90。
    /// </summary>
    public IDictionary<string, double> Capabilities { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private Collection<string> _fallbackChain = new();

    /// <summary>
    /// 显式 fallback 模型链（按优先级的模型名列表）。模型失败后优先按此链切换到指定模型，
    /// 而非自动 tier 降级。用于合规/成本敏感场景需要确定性 fallback。未配置（空）时走自动 tier fallback。
    /// </summary>
    public IList<string> FallbackChain
    {
        get => _fallbackChain;
        set => _fallbackChain = value is null ? new Collection<string>() : new Collection<string>(value);
    }

    /// <summary>
    /// 未显式配置能力时的按维度 tier 回退表。
    /// 维度分两类：语言是「廉价维度」（模型间差距小，档距近扁平），推理/代码是「昂贵维度」（档距陡）。
    /// 这让多维能力路由（<c>EnableMultiDimensionalRouting</c>）在语言任务上可让 cheap 凭价格胜出，
    /// 在推理/代码任务上仍让 strong 凭能力分差胜出。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<ModelTier, double>> DimensionFallbacks =
        new Dictionary<string, IReadOnlyDictionary<ModelTier, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["language"] = new Dictionary<ModelTier, double>
            {
                [ModelTier.Strong] = 0.80,
                [ModelTier.Medium] = 0.78,
                [ModelTier.Cheap] = 0.76
            },
            ["reasoning"] = new Dictionary<ModelTier, double>
            {
                [ModelTier.Strong] = 0.90,
                [ModelTier.Medium] = 0.50,
                [ModelTier.Cheap] = 0.20
            },
            ["coding"] = new Dictionary<ModelTier, double>
            {
                [ModelTier.Strong] = 0.90,
                [ModelTier.Medium] = 0.60,
                [ModelTier.Cheap] = 0.30
            }
        };

    /// <summary>
    /// 获取指定维度的能力评分。优先级：显式 <see cref="Capabilities"/> 配置 → 按 <see cref="Tier"/> 的维度回退表。
    /// 未知维度（非 coding/reasoning/language）保守回退 0.5，不偏向任意档。
    /// </summary>
    public double GetEffectiveCapability(string dimension)
    {
        if (Capabilities is not null && Capabilities.TryGetValue(dimension, out double val))
        {
            return val;
        }
        if (DimensionFallbacks.TryGetValue(dimension, out var byTier)
            && byTier.TryGetValue(Tier, out double fallback))
        {
            return fallback;
        }
        return 0.5;
    }
}
