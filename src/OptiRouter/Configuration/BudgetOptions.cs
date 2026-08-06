namespace OptiRouter.Configuration;

/// <summary>
/// 成本预算相关配置。
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>
    /// 日预算（USD）。
    /// </summary>
    public decimal DailyBudgetUsd { get; set; }

    /// <summary>
    /// 会话预算（USD）。为 null 时表示不限制会话级预算。
    /// </summary>
    public decimal? SessionBudgetUsd { get; set; }

    /// <summary>
    /// 预算耗尽后的行为。
    /// </summary>
    public BudgetExhaustionMode EnforceOnExhausted { get; set; } = BudgetExhaustionMode.Degrade;
}
