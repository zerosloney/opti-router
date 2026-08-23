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
    /// 分布式持久化存储提供者，默认 <c>Auto</c>：配置了 <c>OptiRouter:ConfigDbConnectionString</c>
    /// 即用 MariaDb，否则回退 SQLite 文件——只需配置连接串一处即可全量切换。
    /// 显式指定 <c>Sqlite</c> / <c>MariaDb</c> / <c>Postgres</c> / <c>Redis</c> / <c>InMemory</c>
    /// 可覆盖自动推断；服务器型 DB 供多实例共享全局账本。
    /// </summary>
    public string StoreProvider { get; set; } = "Auto";

    /// <summary>
    /// MariaDB/MySQL 连接字符串，仅作独立库覆盖用；缺省（推荐）回退全局
    /// <c>OptiRouter:ConfigDbConnectionString</c>（同一数据库只配置一处连接）。
    /// 当 <see cref="StoreProvider"/> 为 "MariaDb" 且两者都为空时启动校验失败。
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

    /// <summary>
    /// in-flight 预算预留的输出 token 预估上限。请求发起前按"输入估算成本 + 输出预估成本"
    /// 预扣预算，防止并发请求在计费落账（流结束后）之前集体越过预算线（TOCTOU）；
    /// 请求结束释放预留、按真实用量入账。请求带 max_tokens 时取两者较小值。
    /// 0 = 关闭预留（退回纯事后检查）。默认 4096——估大误拒、估小留窗口的平衡点。
    /// </summary>
    public int ReservationMaxOutputTokens { get; set; } = 4096;
}
