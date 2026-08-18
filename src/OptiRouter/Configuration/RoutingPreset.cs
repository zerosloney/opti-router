using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Configuration;

/// <summary>
/// 路由预设应用器。根据 <see cref="RoutingOptions.Preset"/> 填充未显式配置的项。
/// </summary>
public static class RoutingPreset
{
    /// <summary>
    /// cost-first（成本优先）预设配置。
    /// </summary>
    private static readonly Dictionary<string, object?> CostFirstPreset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EnableThompsonSampling"] = true,
        ["EnableLatencyAware"] = true,
        ["ExplorationEpsilon"] = 0.05,
        ["EnableResponseCache"] = true,
        ["DefaultTier"] = ModelTier.Cheap
    };

    /// <summary>
    /// balanced（均衡）预设配置。
    /// </summary>
    private static readonly Dictionary<string, object?> BalancedPreset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EnableThompsonSampling"] = true,
        ["EnableCascadeUpgrade"] = true,
        ["CascadeUpgradeSampleRate"] = 0.1,
        ["EnableResponseCache"] = true,
        ["DefaultTier"] = ModelTier.Medium
    };

    /// <summary>
    /// quality-first（质量优先）预设配置。
    /// </summary>
    private static readonly Dictionary<string, object?> QualityFirstPreset = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DefaultTier"] = ModelTier.Strong,
        ["EnableFusionRouter"] = true,
        ["EnableByzantineConsensus"] = true,
        ["EnableCascadeUpgrade"] = true,
        ["CascadeUpgradeSampleRate"] = 0.3
    };

    /// <summary>
    /// 返回三档预设的只读视图（预设名 → {配置项 → 值}），供 dashboard 预设入口展示与应用。
    /// enum 值转为字符串，避免序列化为数字。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> GetPresets()
    {
        static IReadOnlyDictionary<string, object?> Normalize(Dictionary<string, object?> preset) =>
            preset.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is Enum e ? e.ToString() : kvp.Value,
                StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["cost-first"] = Normalize(CostFirstPreset),
            ["balanced"] = Normalize(BalancedPreset),
            ["quality-first"] = Normalize(QualityFirstPreset)
        };
    }

    /// <summary>
    /// 应用路由预设到 <see cref="RoutingOptions"/>。
    /// 仅对未在配置文件中显式配置的项进行赋值（显式配置永远赢）。
    /// </summary>
    /// <param name="routing">路由选项实例。</param>
    /// <param name="config">配置根（用于检测显式配置）。</param>
    /// <param name="logger">日志记录器。</param>
    public static void Apply(RoutingOptions routing, IConfiguration config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        string? preset = routing.Preset;
        if (string.IsNullOrWhiteSpace(preset))
        {
            // Preset 为空，直接返回
            return;
        }

        // 获取预设字典（大小写不敏感匹配）
        Dictionary<string, object?>? presetDict = preset.Trim().ToLowerInvariant() switch
        {
            "cost-first" => CostFirstPreset,
            "balanced" => BalancedPreset,
            "quality-first" => QualityFirstPreset,
            _ => null
        };

        if (presetDict is null)
        {
            logger.LogWarning("未知的路由预设名称：'{Preset}'。有效值：cost-first, balanced, quality-first", preset);
            return;
        }

        // 收集被跳过的键（已显式配置）
        var skippedKeys = new List<string>();
        var appliedKeys = new List<string>();

        // 对预设中的每个键进行检查和赋值
        foreach (var (key, value) in presetDict)
        {
            string configKey = $"OptiRouter:Routing:{key}";

            // 检查是否已显式配置
            if (config[configKey] is not null)
            {
                skippedKeys.Add(key);
                continue;
            }

            // 使用反射赋值
            PropertyInfo? property = typeof(RoutingOptions).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite)
            {
                logger.LogWarning("预设包含无效的属性名：'{Key}'，跳过", key);
                continue;
            }

            try
            {
                // 类型转换与赋值
                object convertedValue = value ?? throw new ArgumentNullException(nameof(value));
                if (property.PropertyType != convertedValue.GetType())
                {
                    // 需要类型转换
                    convertedValue = ConvertValue(convertedValue, property.PropertyType);
                }

                property.SetValue(routing, convertedValue);
                appliedKeys.Add(key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "应用预设时无法设置属性 '{Key}' 的值：{Value}", key, value);
            }
        }

        // 日志记录
        if (appliedKeys.Count > 0)
        {
            logger.LogInformation(
                "应用路由预设 '{Preset}'，填充了以下未配置项：{Keys}",
                preset,
                string.Join(", ", appliedKeys));
        }

        if (skippedKeys.Count > 0)
        {
            logger.LogInformation(
                "路由预设 '{Preset}' 中以下项已被显式配置，保持原值：{Keys}",
                preset,
                string.Join(", ", skippedKeys));
        }
    }

    /// <summary>
    /// 将值转换为目标类型。
    /// </summary>
    private static object ConvertValue(object value, Type targetType)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        // 处理可空类型
        Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // 直接类型匹配
        if (underlyingType == value.GetType())
            return value;

        // 枚举类型
        if (underlyingType.IsEnum)
        {
            if (value is string enumString)
            {
                return Enum.Parse(underlyingType, enumString, ignoreCase: true);
            }
            return Enum.ToObject(underlyingType, value);
        }

        // 基础类型转换
        if (underlyingType == typeof(bool) && value is bool boolValue)
            return boolValue;
        if (underlyingType == typeof(double) && value is double doubleValue)
            return doubleValue;
        if (underlyingType == typeof(int) && value is int intValue)
            return intValue;

        // 尝试 IConvertible 转换
        if (value is IConvertible convertible)
        {
            return Convert.ChangeType(convertible, underlyingType);
        }

        throw new InvalidOperationException($"无法将类型 '{value.GetType()}' 转换为 '{targetType}'");
    }
}
