using OptiRouter.Configuration;

namespace OptiRouter.Providers;

/// <summary>
/// 内置 provider 目录，包含经踩坑验证的免费/便宜端点。
/// 仅当配置库为空且 <c>Routing:EnableBuiltInProviderCatalog</c> 显式开启（默认关闭）时，
/// 路由引擎自动注入这些默认值，让 <c>auto</c> 模型从零配置即可工作——
/// 目录均为第三方端点，注入即改变 prompt 数据流向，故默认不注入。
///
/// 目录结构参考 OmniRoute 的 provider catalog，按 tier 分组：
/// - <c>Keyless</c>：无需 API Key，零配置接入
/// - <c>FreeTier</c>：有免费配额，需要 API Key
/// - <c>Cheap</c>：价格极低的付费端点（$0.01/M token 以下）
///
/// 需 Key 的条目注入后 ApiKey 为空，须在管理台 /models 补配密钥后方可调用。
/// </summary>
public static class BuiltInProviderCatalog
{
    /// <summary>
    /// 内置目录键，标记模型来自内置 catalog，不写入用户配置库。
    /// </summary>
    public const string CatalogSourceTag = "__catalog__";

    /// <summary>
    /// 获取所有内置模型（零配置默认注入）。
    /// </summary>
    public static IReadOnlyList<ModelEndpointOptions> GetDefaults()
    {
        var models = new List<ModelEndpointOptions>();

        // ─── Keyless（无需 API Key）────────────────────────────
        // OpenCode Free — OmniRoute 社区维护的免费 Claude 3.5 兼容端点
        models.Add(new ModelEndpointOptions
        {
            Name = "oc/claude-3.5-sonnet",
            Id = "claude-3.5-sonnet",
            BaseUrl = "https://opencode.ai/api/v1",
            ApiKey = null,
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 200_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "opencode",
            Family = "claude",
            Tags = new[] { "vision", "tool-use", "json-mode" },
            Enabled = true,
        });

        // Felo Free — Kimi 社区项目，OpenAI 兼容
        models.Add(new ModelEndpointOptions
        {
            Name = "felo/gpt-4o-mini",
            Id = "gpt-4o-mini",
            BaseUrl = "https://ws.felo.me",
            ApiKey = null,
            Tier = ModelTier.Medium,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 128_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "felo",
            Family = "gpt-4o-mini",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // ─── Free Tier（需注册获取免费 Key）───────────────────
        // Kimi（月之暗面）— 注册送免费 token
        models.Add(new ModelEndpointOptions
        {
            Name = "kimi/moonshot-v1-8k",
            Id = "moonshot-v1-8k",
            BaseUrl = "https://api.moonshot.cn/v1",
            ApiKey = null, // env:KIMI_API_KEY
            Tier = ModelTier.Medium,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 8_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "kimi",
            Family = "moonshot",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // DeepSeek — 免费额度
        models.Add(new ModelEndpointOptions
        {
            Name = "deepseek/deepseek-chat",
            Id = "deepseek-chat",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = null, // env:DEEPSEEK_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 64_000,
            InputPricePerMillion = 0.1m,
            OutputPricePerMillion = 0.28m,
            Provider = "deepseek",
            Family = "deepseek-chat",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Qwen（阿里通义）— 注册送免费 token
        models.Add(new ModelEndpointOptions
        {
            Name = "qwen/qwen-turbo",
            Id = "qwen-turbo",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ApiKey = null, // env:DASHSCOPE_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 128_000,
            InputPricePerMillion = 0.8m,
            OutputPricePerMillion = 2m,
            Provider = "qwen",
            Family = "qwen-turbo",
            Tags = new[] { "tool-use" },
            Enabled = true,
        });

        // Gemini Free — Google 免费层
        models.Add(new ModelEndpointOptions
        {
            Name = "gemini/gemini-2.0-flash",
            Id = "gemini-2.0-flash",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = null, // env:GEMINI_API_KEY
            Tier = ModelTier.Medium,
            Protocol = ProviderProtocol.Gemini,
            MaxContextTokens = 1_048_576,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "google",
            Family = "gemini",
            Tags = new[] { "vision", "tool-use", "json-mode" },
            Enabled = true,
        });

        // MiniMax — 注册送免费 token
        models.Add(new ModelEndpointOptions
        {
            Name = "minimax/minimax-01",
            Id = "MiniMax-Text-01",
            BaseUrl = "https://api.minimax.chat/v1",
            ApiKey = null, // env:MINIMAX_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 256_000,
            InputPricePerMillion = 0.01m,
            OutputPricePerMillion = 0.1m,
            Provider = "minimax",
            Family = "minimax-01",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        return models;
    }

    /// <summary>
    /// 按 tier 分组返回目录摘要，供 Dashboard 显示。
    /// </summary>
    public static ProviderCatalogSummary GetSummary()
    {
        var defaults = GetDefaults();
        return new ProviderCatalogSummary
        {
            Total = defaults.Count,
            ByTier = defaults
                .GroupBy(m => m.Tier)
                .ToDictionary(g => g.Key, g => g.ToList()),
            ByProvider = defaults
                .GroupBy(m => m.Provider)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Name).ToList()),
        };
    }
}

/// <summary>
/// 目录摘要数据结构。
/// </summary>
public sealed class ProviderCatalogSummary
{
    public int Total { get; init; }
    public required Dictionary<ModelTier, List<ModelEndpointOptions>> ByTier { get; init; }
    public required Dictionary<string, List<string>> ByProvider { get; init; }
}
