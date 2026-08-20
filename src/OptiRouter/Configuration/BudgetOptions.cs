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
    /// 是否启用持久化成本账本，跨进程重启保留日/会话花费。false 时使用内存实现，重启即丢失。
    /// 服务器型提供者（MariaDb/Postgres/Redis）不受此开关影响，始终走对应 DB。
    /// </summary>
    public bool UsePersistentStore { get; set; } = true;

    /// <summary>
    /// SQLite 账本文件路径。仅 StoreProvider=Sqlite 且 UsePersistentStore=true 时生效。
    /// 目录不存在会自动创建。
    /// </summary>
    public string StorePath { get; set; } = "data/optirouter-budget.db";

    /// <summary>
    /// 会话账户淘汰年龄（小时）。超过此时间无活动的会话账户自动清理，防止内存/DICT 无界增长。
    /// 0 或负值表示禁用淘汰。
    /// </summary>
    public int SessionEvictionHours { get; set; } = 24;

    /// <summary>
    /// 分布式持久化存储提供者："Sqlite" | "MariaDb" | "Postgres" | "Redis" | "InMemory"。默认 "Sqlite"。
    /// 对于 Kubernetes 多节点无状态部署场景，可切换为 "MariaDb"、"Postgres" 或 "Redis" 共享全局账本。
    /// </summary>
    public string StoreProvider { get; set; } = "Sqlite";

    /// <summary>
    /// MariaDB/MySQL 连接字符串。当 <see cref="StoreProvider"/> 为 "MariaDb" 时必填。
    /// </summary>
    public string? MariaDbConnectionString { get; set; } = null;

    /// <summary>
    /// PostgreSQL 连接字符串。当 <see cref="StoreProvider"/> 为 "Postgres" 时必填。
    /// </summary>
    public string? PostgresConnectionString { get; set; } = null;

    /// <summary>
    /// Redis 连接字符串。当 <see cref="StoreProvider"/> 为 "Redis" 时必填。
    /// </summary>
    public string? RedisConnectionString { get; set; } = null;

    /// <summary>
    /// Redis 键前缀。默认 "optirouter:"。
    /// </summary>
    public string RedisKeyPrefix { get; set; } = "optirouter:";
}
