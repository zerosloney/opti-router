using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Routing;

namespace OptiRouter.Configuration;

/// <summary>
/// 对 <see cref="RouterOptions"/> 启动时校验，确保配置合法。
/// </summary>
public sealed class RouterOptionsValidator : IValidateOptions<RouterOptions>
{
    private readonly ILogger<RouterOptionsValidator> _logger;

    /// <summary>
    /// 用默认（null）logger 构造，保持测试零改动。
    /// </summary>
    public RouterOptionsValidator() : this(NullLogger<RouterOptionsValidator>.Instance) { }

    /// <summary>
    /// 用指定 logger 构造，用于 Tags 软校验警告输出。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public RouterOptionsValidator(ILogger<RouterOptionsValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RouterOptions options)
    {
        if (options.Models is null || options.Models.Count == 0)
        {
            return ValidateOptionsResult.Fail("Models 不能为空，至少配置一个模型端点。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                return ValidateOptionsResult.Fail(
                    "每个模型端点都必须有非空 Name 或 Id（只配置 Id 时 Name 自动生成为「供应商/模型」）。");
            }

            if (!names.Add(model.Name))
            {
                return ValidateOptionsResult.Fail($"模型 Name 必须唯一，重复值: {model.Name}。");
            }

            string? modelError = ValidateModel(model);
            if (modelError is not null)
            {
                return ValidateOptionsResult.Fail(modelError);
            }
        }

        if (options.Routing.LongInputThresholdTokens <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.LongInputThresholdTokens 必须大于 0。");
        }

        if (!string.Equals(options.Budget.StoreProvider, "Auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Budget.StoreProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Budget.StoreProvider, "MariaDb", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Budget.StoreProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Budget.StoreProvider, "Redis", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Budget.StoreProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Budget.StoreProvider 必须是 'Auto'、'Sqlite'、'MariaDb'、'Postgres'、'Redis' 或 'InMemory'。");
        }

        if (string.Equals(options.Budget.StoreProvider, "MariaDb", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.Budget.MariaDbConnectionString))
        {
            return ValidateOptionsResult.Fail(
                "Budget.MariaDbConnectionString 不能为空（当 StoreProvider 为 MariaDb 时）。");
        }

        if (string.Equals(options.Budget.StoreProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.Budget.PostgresConnectionString))
        {
            return ValidateOptionsResult.Fail(
                "Budget.PostgresConnectionString 不能为空（当 StoreProvider 为 Postgres 时）。");
        }

        if (string.Equals(options.Budget.StoreProvider, "Redis", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.Budget.RedisConnectionString))
        {
            return ValidateOptionsResult.Fail(
                "Budget.RedisConnectionString 不能为空（当 StoreProvider 为 Redis 时）。");
        }

        if (string.Equals(options.Budget.StoreProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            && options.Budget.UsePersistentStore
            && string.IsNullOrWhiteSpace(options.Budget.StorePath))
        {
            return ValidateOptionsResult.Fail("Budget.StorePath 不能为空（当 UsePersistentStore 为 true 时）。");
        }

        if (options.Budget.SessionEvictionHours < 0)
        {
            return ValidateOptionsResult.Fail("Budget.SessionEvictionHours 不能为负数。");
        }

        if (options.Budget.ReservationMaxOutputTokens < 0)
        {
            return ValidateOptionsResult.Fail("Budget.ReservationMaxOutputTokens 不能为负数（0 = 关闭 in-flight 预算预留）。");
        }

        if (options.Routing.FailoverHalfOpenMaxProbes <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.FailoverHalfOpenMaxProbes 必须大于 0。");
        }

        if (options.Routing.FailoverHalfOpenRequiredSuccesses <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.FailoverHalfOpenRequiredSuccesses 必须大于 0。");
        }

        if (options.Routing.FailoverGlobalTimeoutSeconds < 0)
        {
            return ValidateOptionsResult.Fail("Routing.FailoverGlobalTimeoutSeconds 不能为负数。");
        }

        if (options.Routing.StreamFirstTokenTimeoutMs < 0)
        {
            return ValidateOptionsResult.Fail("Routing.StreamFirstTokenTimeoutMs 不能为负数。");
        }

        if (options.Routing.ResponseCacheMaxBytes < 0)
            return ValidateOptionsResult.Fail("Routing.ResponseCacheMaxBytes 不能为负数（0 = 不限字节）。");

        if (options.Routing.EnableResponseCache)
        {
            if (options.Routing.ResponseCacheTtlSeconds <= 0)
                return ValidateOptionsResult.Fail("Routing.ResponseCacheTtlSeconds 必须大于 0（启用响应缓存时）。");
            if (options.Routing.ResponseCacheMaxEntries <= 0)
                return ValidateOptionsResult.Fail("Routing.ResponseCacheMaxEntries 必须大于 0（启用响应缓存时）。");
        }

        if (options.Routing.CostAwareWeight < 0 || options.Routing.CostAwareWeight > 1)
            return ValidateOptionsResult.Fail("Routing.CostAwareWeight 必须在 [0.0, 1.0] 范围内。");
        if (options.Routing.CostAwareWeight > 0 && options.Routing.CostAwareBaselineUsd <= 0)
            return ValidateOptionsResult.Fail("Routing.CostAwareBaselineUsd 必须大于 0（启用成本感知时）。");

        // 质量惩罚因子：越界会让乘性合成 reward 超出 [0,1]，扭曲 Beta 分布。
        if (options.Routing.QualityPenaltyFactor < 0.0 || options.Routing.QualityPenaltyFactor > 1.0)
            return ValidateOptionsResult.Fail("Routing.QualityPenaltyFactor 必须在 [0.0, 1.0] 范围内。");

        // LLM-as-judge：采样率越界同样扭曲 reward 与成本预算（>1 无意义，<0 视为配置错误而非关闭）。
        if (options.Routing.QualityJudgeSampleRate < 0.0 || options.Routing.QualityJudgeSampleRate > 1.0)
            return ValidateOptionsResult.Fail("Routing.QualityJudgeSampleRate 必须在 [0.0, 1.0] 范围内。");

        // regenerate 负反馈：注入的 reward 必须落在 [0,1]；窗口 <=0 会让所有同键请求都判为 regenerate。
        if (options.Routing.EnableRegenerateFeedback)
        {
            if (options.Routing.RegeneratePenaltyReward < 0.0 || options.Routing.RegeneratePenaltyReward > 1.0)
                return ValidateOptionsResult.Fail("Routing.RegeneratePenaltyReward 必须在 [0.0, 1.0] 范围内（启用 regenerate 负反馈时）。");
            if (options.Routing.RegenerateFeedbackWindowSeconds <= 0)
                return ValidateOptionsResult.Fail("Routing.RegenerateFeedbackWindowSeconds 必须大于 0（启用 regenerate 负反馈时）。");
        }

        if (options.Routing.HealthProbeIntervalSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.HealthProbeIntervalSeconds 必须大于 0。");
        }

        if (options.Routing.TokenEstimation == TokenEstimationMode.Tiktoken
            && !TiktokenTokenEstimator.IsEncodingAvailable(options.Routing.TiktokenEncoding))
        {
            return ValidateOptionsResult.Fail(
                $"Routing.TiktokenEncoding '{options.Routing.TiktokenEncoding}' 不是可用的 tiktoken 编码。" +
                "常见取值：o200k_base、cl100k_base。");
        }

        if (options.Routing.LatencyMinSamples < 0)
        {
            return ValidateOptionsResult.Fail("Routing.LatencyMinSamples 不能为负数。");
        }

        if (options.Routing.LatencyStatsWindowMinutes <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.LatencyStatsWindowMinutes 必须大于 0。");
        }

        if (options.Routing.FusionMaxParallel < 2 || options.Routing.FusionMaxParallel > 5)
        {
            return ValidateOptionsResult.Fail("Routing.FusionMaxParallel 必须在 [2, 5] 范围内。");
        }

        if (options.Routing.FusionHedgeDelayMs < 0)
        {
            return ValidateOptionsResult.Fail("Routing.FusionHedgeDelayMs 不能为负数。");
        }

        if (options.Routing.FusionRouterPanelSize < 2 || options.Routing.FusionRouterPanelSize > 5)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterPanelSize 必须在 [2, 5] 范围内。");
        }

        if (options.Routing.FusionRouterMinPanelSize < 2 || options.Routing.FusionRouterMinPanelSize > 5)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterMinPanelSize 必须在 [2, 5] 范围内。");
        }

        if (options.Routing.FusionRouterMinPanelSize > options.Routing.FusionRouterPanelSize)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterMinPanelSize 不能大于 FusionRouterPanelSize。");
        }

        if (options.Routing.PromptCacheAffinityTtlSeconds <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.PromptCacheAffinityTtlSeconds 必须大于 0。");
        }

        if (options.Routing.SemanticSimilarityThreshold < 0 || options.Routing.SemanticSimilarityThreshold > 1)
        {
            return ValidateOptionsResult.Fail("Routing.SemanticSimilarityThreshold 必须在 [0.0, 1.0] 范围内。");
        }

        if (options.Routing.HybridHighConfidenceThreshold < 0 || options.Routing.HybridHighConfidenceThreshold > 1)
        {
            return ValidateOptionsResult.Fail("Routing.HybridHighConfidenceThreshold 必须在 [0.0, 1.0] 范围内。");
        }

        if (!string.Equals(options.Routing.SemanticRouterMode, "TfIdf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Routing.SemanticRouterMode, "Hybrid", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Routing.SemanticRouterMode, "Dense", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail("Routing.SemanticRouterMode 必须是 'Hybrid'、'TfIdf' 或 'Dense'。");
        }

        if (options.Routing.EnableFusionRouter && options.Routing.FusionRouterMaxOutputTokens <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterMaxOutputTokens 必须大于 0（启用融合路由时）。");
        }

        if (options.Routing.FusionRouterTemperature < 0 || options.Routing.FusionRouterTemperature > 2)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterTemperature 必须在 [0, 2] 范围内。");
        }

        if (options.Routing.FusionRouterPanelTemperature is { } pt
            && (pt < 0 || pt > 2))
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterPanelTemperature 必须在 [0, 2] 范围内（或为 null 沿用 FusionRouterTemperature）。");
        }

        if (!Enum.IsDefined(typeof(OptiRouter.Routing.RequestComplexity), options.Routing.FusionRouterMinComplexity))
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterMinComplexity 必须是合法的 RequestComplexity 枚举值。");
        }

        if (options.Routing.FusionRouterPanelTimeoutSeconds < 0)
        {
            return ValidateOptionsResult.Fail("Routing.FusionRouterPanelTimeoutSeconds 不能为负数。");
        }

        if (options.Routing.MaxResponseStreamBytes <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.MaxResponseStreamBytes 必须大于 0。");
        }

        // Thompson Sampling 参数校验：target<=0 会把所有成功判为坏（Beta-only），饿死全部模型；
        // discount 越界导致衰减失效或过激。文档承诺范围 [0.5, 0.99]。
        if (options.Routing.EnableThompsonSampling)
        {
            if (options.Routing.ThompsonLatencyTargetMs <= 0)
            {
                return ValidateOptionsResult.Fail("Routing.ThompsonLatencyTargetMs 必须大于 0（启用 Thompson Sampling 时）。");
            }
            if (options.Routing.ThompsonDiscountFactor < 0.5 || options.Routing.ThompsonDiscountFactor > 0.99)
            {
                return ValidateOptionsResult.Fail("Routing.ThompsonDiscountFactor 必须在 [0.5, 0.99] 范围内（启用 Thompson Sampling 时）。");
            }
            if (options.Routing.ThompsonRaceCancelledReward < 0.0 || options.Routing.ThompsonRaceCancelledReward > 1.0)
            {
                return ValidateOptionsResult.Fail("Routing.ThompsonRaceCancelledReward 必须在 [0.0, 1.0] 范围内（启用 Thompson Sampling 时）。");
            }
            // Per-tier 延迟目标：<=0 会让该 tier 的所有成功都压在地板分，等同饿死。
            foreach (var pair in options.Routing.ThompsonLatencyTargetMsByTier)
            {
                if (pair.Value <= 0)
                {
                    return ValidateOptionsResult.Fail($"Routing.ThompsonLatencyTargetMsByTier[{pair.Key}] 必须大于 0（启用 Thompson Sampling 时）。");
                }
            }
        }

        // 级联质量 reward 校验：注入 Thompson/Bandit 的 reward 必须落在 [0,1]，否则
        // 越界值会扭曲 Beta 分布（负值饿死、>1 过激）。仅 Cascade 启用时才注入，故此时校验。
        if (options.Routing.EnableCascadeUpgrade)
        {
            if (options.Routing.CascadeUpgradeConfidentReward < 0.0 || options.Routing.CascadeUpgradeConfidentReward > 1.0)
            {
                return ValidateOptionsResult.Fail("Routing.CascadeUpgradeConfidentReward 必须在 [0.0, 1.0] 范围内（启用级联升级时）。");
            }
            if (options.Routing.CascadeUpgradeUncertainReward < 0.0 || options.Routing.CascadeUpgradeUncertainReward > 1.0)
            {
                return ValidateOptionsResult.Fail("Routing.CascadeUpgradeUncertainReward 必须在 [0.0, 1.0] 范围内（启用级联升级时）。");
            }
        }

        // 上下文老虎机（LinUCB）参数校验：alpha<=0 会关闭探索（纯利用，冷启动饿死）；
        // discount 越界导致衰减失效或过激。
        // 与 Thompson Sampling 互斥——同一请求段内只能由一种重排策略负责，混用会让两类
        // 状态互相覆盖、stat 计数器错位。启动期拒绝比运行时静默 cascade 更安全。
        if (options.Routing.EnableContextualBandit
            && options.Routing.EnableThompsonSampling)
        {
            return ValidateOptionsResult.Fail(
                "EnableContextualBandit 与 EnableThompsonSampling 互斥，不能同时开启。" +
                "LinUCB 在启用时段内替代 Thompson，请只开启其中一个。");
        }

        if (options.Routing.EnableLoadBalance
            && (options.Routing.EnableContextualBandit
                || options.Routing.EnableThompsonSampling
                || options.Routing.EnableLatencyAware))
        {
            return ValidateOptionsResult.Fail(
                "EnableLoadBalance 与 Contextual Bandit、Thompson Sampling、LatencyAware 排序互斥。" +
                "负载均衡会覆盖同 tier 的学习/延迟排序，请只启用一种排序所有者。");
        }
        if (options.Routing.EnableContextualBandit)
        {
            if (options.Routing.ContextualBanditAlpha <= 0)
            {
                return ValidateOptionsResult.Fail("Routing.ContextualBanditAlpha 必须大于 0（启用 Contextual Bandit 时）。");
            }
            if (options.Routing.ContextualBanditDiscountFactor < 0.5 || options.Routing.ContextualBanditDiscountFactor > 0.99)
            {
                return ValidateOptionsResult.Fail("Routing.ContextualBanditDiscountFactor 必须在 [0.5, 0.99] 范围内（启用 Contextual Bandit 时）。");
            }
        }

        // ε 探索保底：越界会让探索概率语义失效（负值禁用、>1 恒探索）。
        if (options.Routing.ExplorationEpsilon < 0.0 || options.Routing.ExplorationEpsilon > 1.0)
            return ValidateOptionsResult.Fail("Routing.ExplorationEpsilon 必须在 [0.0, 1.0] 范围内。");

        // 探索饥饿阈值：负数无语义（>=0，0=关闭定向探索）。
        if (options.Routing.ExplorationStarvedN < 0)
            return ValidateOptionsResult.Fail("Routing.ExplorationStarvedN 不能为负数。");

        // 延迟归一化基准 token 数：负数无语义（>=0，0=禁用）。
        if (options.Routing.ThompsonLatencyNormalizeRefTokens < 0)
            return ValidateOptionsResult.Fail("Routing.ThompsonLatencyNormalizeRefTokens 不能为负数。");

        // 审计保留时长校验：0 = 永久保留（AuditRetentionService 跳过淘汰），负数无语义。
        if (options.Routing.AuditRetentionHours < 0)
        {
            return ValidateOptionsResult.Fail("Routing.AuditRetentionHours 必须 >= 0（0 表示永久保留）。");
        }

        // Tags 软校验：未识别的 tag 仅 warning，不阻断启动。
        // 允许自定义 tag（未来扩展），但提示拼写错误（如 "vison" 应为 "vision"）。
        // 仅当启用能力过滤时有意义，但始终提示——配置错误在启用前就应发现。
        var unknownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in options.Models)
        {
            if (model.Tags is null) continue;
            foreach (var tag in model.Tags)
            {
                if (!ModelCapabilities.KnownTags.Contains(tag))
                    unknownTags.Add(tag);
            }
        }
        if (unknownTags.Count > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "检测到未识别的模型 Tags: {Unknown}。已知标签: {Known}。" +
                "若为拼写错误，CapabilityFilter 将无法匹配；自定义 tag 不影响其他策略。",
                string.Join(", ", unknownTags), string.Join(", ", ModelCapabilities.KnownTags));
        }

        // FallbackChain 引用校验：链中模型名必须指向已配置的模型。
        var knownModelNames = new HashSet<string>(options.Models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var model in options.Models)
        {
            if (model.FallbackChain is null) continue;
            foreach (var chainName in model.FallbackChain)
            {
                if (!knownModelNames.Contains(chainName))
                    return ValidateOptionsResult.Fail($"模型 {model.Name} 的 FallbackChain 引用了不存在的模型 '{chainName}'。");
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// SSRF 防线：网关会携带 ApiKey 真实请求 BaseUrl，云元数据端点与链路本地网段
    /// （169.254.0.0/16、IPv6 link-local）禁止作为上游。本地 LLM（localhost/127.0.0.1）不受影响。
    /// </summary>
    private static bool IsBlockedUpstreamHost(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        if (host is "metadata.google.internal" or "metadata.goog")
            return true;
        if (!System.Net.IPAddress.TryParse(host, out var ip))
            return false;
        if (ip.IsIPv6LinkLocal)
            return true;
        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>
    /// 校验单个模型端点的数值边界（价格非负、MaxContextTokens>0）。
    /// 供启动校验与 Dashboard 写入复用，确保两条路径一致。
    /// </summary>
    /// <param name="model">待校验模型。</param>
    /// <returns>错误消息（含模型名）；null 表示通过。</returns>
    public static string? ValidateModel(ModelEndpointOptions model)
    {
        if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return $"模型 {model.Name} 的 BaseUrl 必须是绝对 HTTP/HTTPS URI。";
        }
        if (IsBlockedUpstreamHost(baseUri))
        {
            return $"模型 {model.Name} 的 BaseUrl 指向云元数据/链路本地地址，禁止配置。";
        }

        if (model.InputPricePerMillion < 0)
            return $"模型 {model.Name} 的 InputPricePerMillion 不能为负数。";

        if (model.OutputPricePerMillion < 0)
            return $"模型 {model.Name} 的 OutputPricePerMillion 不能为负数。";

        if (model.CachedInputPricePerMillion < 0)
            return $"模型 {model.Name} 的 CachedInputPricePerMillion 不能为负数。";

        if (model.CacheWriteInputPricePerMillion < 0)
            return $"模型 {model.Name} 的 CacheWriteInputPricePerMillion 不能为负数。";

        if (model.MaxContextTokens <= 0)
            return $"模型 {model.Name} 的 MaxContextTokens 必须大于 0。";

        if (model.Weight < 0)
            return $"模型 {model.Name} 的 Weight 必须大于等于 0。";

        if (model.TimeoutSeconds <= 0)
            return $"模型 {model.Name} 的 TimeoutSeconds 必须大于 0。";

        if (model.MaxRetries < 0)
            return $"模型 {model.Name} 的 MaxRetries 不能为负数。";

        if (model.FallbackChain is not null && model.FallbackChain.Count > 0)
        {
            if (model.FallbackChain.Any(n => string.Equals(n, model.Name, StringComparison.OrdinalIgnoreCase)))
                return $"模型 {model.Name} 的 FallbackChain 不能包含自身。";
            if (model.FallbackChain.Distinct(StringComparer.OrdinalIgnoreCase).Count() != model.FallbackChain.Count)
                return $"模型 {model.Name} 的 FallbackChain 包含重复模型名。";
        }

        return null;
    }
}
