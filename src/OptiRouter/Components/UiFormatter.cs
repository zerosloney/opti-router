namespace OptiRouter.Components;

/// <summary>
/// 前端 UI 格式化辅助类：统一数字、金额、延迟、百分比与 Token 的人类可读格式化，消除视觉噪点。
/// </summary>
public static class UiFormatter
{
    /// <summary>
    /// 智能金额格式化 (decimal)：
    /// - 0 元显示 $0.00
    /// - 大于等于 1.00 美元显示两位小数 (如 $1.25)
    /// - 大于等于 0.01 美元显示四位小数 (如 $0.0450)
    /// - 小于 0.01 美元且大于 0 显示六位微量小数 (如 $0.000342)
    /// </summary>
    public static string FormatCost(decimal cost)
    {
        if (cost == 0m) return "$0.00";
        if (cost >= 1.00m) return $"${cost:N2}";
        if (cost >= 0.01m) return $"${cost:F4}";
        return $"${cost:F6}";
    }

    /// <summary>
    /// 智能金额格式化 (double 重载)。
    /// </summary>
    public static string FormatCost(double cost)
    {
        if (cost == 0.0) return "$0.00";
        if (cost >= 1.0) return $"${cost:N2}";
        if (cost >= 0.01) return $"${cost:F4}";
        return $"${cost:F6}";
    }

    /// <summary>
    /// 智能大数字紧凑缩写（如 1.25M, 45.2k）。
    /// </summary>
    public static string FormatCompactNumber(long value)
    {
        if (value >= 1_000_000_000)
            return $"{(value / 1_000_000_000.0):F2}B";
        if (value >= 1_000_000)
            return $"{(value / 1_000_000.0):F2}M";
        if (value >= 10_000)
            return $"{(value / 1_000.0):F1}k";
        return value.ToString("N0");
    }

    /// <summary>
    /// 千分位数字格式化。
    /// </summary>
    public static string FormatNumber(long value) => value.ToString("N0");

    /// <summary>
    /// 千分位数字格式化 (int 重载)。
    /// </summary>
    public static string FormatNumber(int value) => value.ToString("N0");

    /// <summary>
    /// 延迟时间格式化（毫秒/秒）。
    /// </summary>
    public static string FormatLatency(double latencyMs)
    {
        if (latencyMs <= 0) return "--";
        if (latencyMs >= 10_000) return $"{(latencyMs / 1000.0):F1} s";
        if (latencyMs >= 1000) return $"{(latencyMs / 1000.0):F2} s";
        return $"{latencyMs:F0} ms";
    }

    /// <summary>
    /// 百分比格式化（保留一位小数）。
    /// </summary>
    public static string FormatPercent(double percentage) => $"{percentage:F1}%";

    /// <summary>
    /// 百分比格式化 (decimal 重载)。
    /// </summary>
    public static string FormatPercent(decimal percentage) => $"{percentage:F1}%";
}
