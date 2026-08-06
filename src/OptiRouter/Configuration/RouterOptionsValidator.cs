using Microsoft.Extensions.Options;

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

        return ValidateOptionsResult.Success;
    }
}
