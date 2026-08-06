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

    /// <summary>
    /// 是否启用持久化成本账本（SQLite）。true 时使用 <see cref="StorePath"/> 指定的文件，
    /// 跨进程重启保留日/会话花费。false 时使用内存实现，重启即丢失。
    /// </summary>
    public bool UsePersistentStore { get; set; } = true;

    /// <summary>
    /// SQLite 账本文件路径。仅在 <see cref="UsePersistentStore"/> 为 true 时生效。
    /// 目录不存在会自动创建。
    /// </summary>
    public string StorePath { get; set; } = "data/optirouter-budget.db";

    /// <summary>
    /// 会话账户淘汰年龄（小时）。超过此时间无活动的会话账户自动清理，防止内存/DICT 无界增长。
    /// 0 或负值表示禁用淘汰。
    /// </summary>
    public int SessionEvictionHours { get; set; } = 24;
}
