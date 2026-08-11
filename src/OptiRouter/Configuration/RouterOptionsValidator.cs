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
                return ValidateOptionsResult.Fail("每个模型端点都必须有非空 Name。");
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

        if (options.Budget.UsePersistentStore && string.IsNullOrWhiteSpace(options.Budget.StorePath))
        {
            return ValidateOptionsResult.Fail("Budget.StorePath 不能为空（当 UsePersistentStore 为 true 时）。");
        }

        if (options.Budget.SessionEvictionHours < 0)
        {
            return ValidateOptionsResult.Fail("Budget.SessionEvictionHours 不能为负数。");
        }

        if (options.Routing.FailoverHalfOpenMaxProbes <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.FailoverHalfOpenMaxProbes 必须大于 0。");
        }

        if (options.Routing.FailoverHalfOpenRequiredSuccesses <= 0)
        {
            return ValidateOptionsResult.Fail("Routing.FailoverHalfOpenRequiredSuccesses 必须大于 0。");
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
        }

        // 上下文老虎机（LinUCB）参数校验：alpha<=0 会关闭探索（纯利用，冷启动饿死）；
        // discount 越界导致衰减失效或过激。与 Thompson 互斥（启用时段内用 LinUCB）。
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

        // 审计保留时长校验：<=0 会让后台服务每次循环淘汰全部审计（AlertEngine 失去数据源）。
        if (options.Routing.AuditRetentionHours < 1)
        {
            return ValidateOptionsResult.Fail("Routing.AuditRetentionHours 必须 >= 1。");
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

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// 校验单个模型端点的数值边界（价格非负、MaxContextTokens>0）。
    /// 供启动校验与 Dashboard 写入复用，确保两条路径一致。
    /// </summary>
    /// <param name="model">待校验模型。</param>
    /// <returns>错误消息（含模型名）；null 表示通过。</returns>
    public static string? ValidateModel(ModelEndpointOptions model)
    {
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

        return null;
    }
}
