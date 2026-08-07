using Microsoft.Extensions.Options;
using OptiRouter.Routing;

namespace OptiRouter.Configuration;

/// <summary>
/// 对 <see cref="RouterOptions"/> 启动时校验，确保配置合法。
/// </summary>
public sealed class RouterOptionsValidator : IValidateOptions<RouterOptions>
{
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

            if (model.InputPricePerMillion < 0)
            {
                return ValidateOptionsResult.Fail($"模型 {model.Name} 的 InputPricePerMillion 不能为负数。");
            }

            if (model.OutputPricePerMillion < 0)
            {
                return ValidateOptionsResult.Fail($"模型 {model.Name} 的 OutputPricePerMillion 不能为负数。");
            }

            if (model.MaxContextTokens <= 0)
            {
                return ValidateOptionsResult.Fail($"模型 {model.Name} 的 MaxContextTokens 必须大于 0。");
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

        return ValidateOptionsResult.Success;
    }
}
