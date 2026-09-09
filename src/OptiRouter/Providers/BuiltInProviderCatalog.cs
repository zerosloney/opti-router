using OptiRouter.Configuration;

namespace OptiRouter.Providers;

/// <summary>
/// 内置 provider 目录，包含经踩坑验证的免费/便宜端点。
/// 仅当配置库为空且 <c>Routing:EnableBuiltInProviderCatalog</c> 显式开启（默认关闭）时，
/// 路由引擎自动注入这些默认值，让 <c>auto</c> 模型从零配置即可工作——
/// 目录均为第三方端点，注入即改变 prompt 数据流向，故默认不注入。
///
/// 目录结构参考 OmniRoute 的 provider catalog，按 tier 分组：
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

        // ─── Free Tier（需注册获取免费 Key）───────────────────
        // OpenCode Zen — Claude Sonnet 5 兼容端点。旧匿名网关 /api/v1 已下线（实测返回官网 HTML），
        // 统一走 Zen 网关，须在 opencode.ai 控制台取 Key；模型 ID 以 GET /zen/v1/models 实时列表为准。
        models.Add(new ModelEndpointOptions
        {
            Name = "oc/claude-sonnet-5",
            Id = "claude-sonnet-5",
            BaseUrl = "https://opencode.ai/zen/v1",
            ApiKey = null, // env:OPENCODE_API_KEY
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_000_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "opencode",
            Family = "claude-sonnet",
            Tags = new[] { "vision", "tool-use", "json-mode" },
            Enabled = true,
        });

        // OpenCode Zen — DeepSeek V4 Flash 免费档（-free 后缀为 Zen 官方免费模型）
        models.Add(new ModelEndpointOptions
        {
            Name = "oc/deepseek-v4-flash-free",
            Id = "deepseek-v4-flash-free",
            BaseUrl = "https://opencode.ai/zen/v1",
            ApiKey = null, // env:OPENCODE_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_300_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "opencode",
            Family = "deepseek-v4",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Kimi K3（月之暗面）— 当前前沿
        models.Add(new ModelEndpointOptions
        {
            Name = "kimi/kimi-k3",
            Id = "kimi-k3",
            BaseUrl = "https://api.moonshot.cn/v1",
            ApiKey = null, // env:KIMI_API_KEY
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 256_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "kimi",
            Family = "kimi-k3",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // DeepSeek V4 Flash — 当前前沿
        models.Add(new ModelEndpointOptions
        {
            Name = "deepseek/deepseek-v4-flash",
            Id = "deepseek-v4-flash",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = null, // env:DEEPSEEK_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_300_000,
            InputPricePerMillion = 0.07m,
            OutputPricePerMillion = 0.28m,
            Provider = "deepseek",
            Family = "deepseek-v4",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Qwen3.7 Max（阿里通义）— 注册送免费 token
        models.Add(new ModelEndpointOptions
        {
            Name = "qwen/qwen3.7-max",
            Id = "qwen3.7-max",
            BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ApiKey = null, // env:DASHSCOPE_API_KEY
            Tier = ModelTier.Medium,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_000_000,
            InputPricePerMillion = 0.8m,
            OutputPricePerMillion = 2m,
            Provider = "qwen",
            Family = "qwen3.7",
            Tags = new[] { "tool-use" },
            Enabled = true,
        });

        // Gemini 3 Flash Free — Google 免费层
        models.Add(new ModelEndpointOptions
        {
            Name = "gemini/gemini-3-flash-free",
            Id = "gemini-3-flash",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = null, // env:GEMINI_API_KEY
            Tier = ModelTier.Medium,
            Protocol = ProviderProtocol.Gemini,
            MaxContextTokens = 1_000_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "google",
            Family = "gemini-3",
            Tags = new[] { "vision", "tool-use", "json-mode" },
            Enabled = true,
        });

        // MiniMax M3 — 当前前沿
        models.Add(new ModelEndpointOptions
        {
            Name = "minimax/minimax-m3",
            Id = "MiniMax-Text-01",
            BaseUrl = "https://api.minimax.chat/v1",
            ApiKey = null, // env:MINIMAX_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_000_000,
            InputPricePerMillion = 0.01m,
            OutputPricePerMillion = 0.1m,
            Provider = "minimax",
            Family = "minimax-m3",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // ─── 前沿补全（2026-09 新增）─────────────────────────
        // GLM 5.3 Flash（智谱）— 中文前沿，超便宜
        models.Add(new ModelEndpointOptions
        {
            Name = "zhipu/glm-5.3-flash",
            Id = "glm-5.3-flash",
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ApiKey = null, // env:ZHIPU_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_300_000,
            InputPricePerMillion = 0.075m,
            OutputPricePerMillion = 0.25m,
            Provider = "zhipu",
            Family = "glm-5.3",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // GLM 5.3（智谱）— 中文强档
        models.Add(new ModelEndpointOptions
        {
            Name = "zhipu/glm-5.3",
            Id = "glm-5.3",
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ApiKey = null, // env:ZHIPU_API_KEY
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_300_000,
            InputPricePerMillion = 2.9m,
            OutputPricePerMillion = 2.9m,
            Provider = "zhipu",
            Family = "glm-5.3",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Hy3 Preview（腾讯）— 9 月新发布，免费 tier
        models.Add(new ModelEndpointOptions
        {
            Name = "tencent/hy3-preview",
            Id = "hy3-preview",
            BaseUrl = "https://api.hunyuan.cloud.tencent.com/v1",
            ApiKey = null, // env:HUNYUAN_API_KEY
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 1_000_000,
            InputPricePerMillion = 0m,
            OutputPricePerMillion = 0m,
            Provider = "tencent",
            Family = "hy3",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Step 3.5 Flash（阶跃星辰）— 中文友好
        models.Add(new ModelEndpointOptions
        {
            Name = "stepfun/step-3.5-flash",
            Id = "step-3.5-flash",
            BaseUrl = "https://api.stepfun.com/v1",
            ApiKey = null, // env:STEPFUN_API_KEY
            Tier = ModelTier.Cheap,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 262_000,
            InputPricePerMillion = 0.1m,
            OutputPricePerMillion = 0.3m,
            Provider = "stepfun",
            Family = "step-3.5",
            Tags = new[] { "tool-use", "json-mode" },
            Enabled = true,
        });

        // Grok 4.5（xAI）— 实时信息
        models.Add(new ModelEndpointOptions
        {
            Name = "xai/grok-4.5",
            Id = "grok-4.5",
            BaseUrl = "https://api.x.ai/v1",
            ApiKey = null, // env:XAI_API_KEY
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.OpenAI,
            MaxContextTokens = 500_000,
            InputPricePerMillion = 5m,
            OutputPricePerMillion = 15m,
            Provider = "xai",
            Family = "grok-4.5",
            Tags = new[] { "vision", "tool-use", "json-mode" },
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
