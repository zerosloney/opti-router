namespace OptiRouter.Routing;

/// <summary>
/// 成本账本持久化抽象。
/// 日预算按 UTC 日期键存储；会话预算按 sessionId 键存储。
/// 实现必须保证 <see cref="AddDaily"/> / <see cref="AddSession"/> 的累加是原子的，
/// 并返回累加后的最新累计值。
/// </summary>
public interface ICostLedgerStore : ICircuitStateStore, IDisposable
{
    /// <summary>
    /// 原子累加日花费，返回累加后的当日总花费。
    /// </summary>
    /// <param name="utcDate">UTC 日期（仅日期部分有效）。</param>
    /// <param name="delta">增量（USD，非负）。</param>
    /// <returns>累加后当日总花费。</returns>
    decimal AddDaily(DateTime utcDate, decimal delta);

    /// <summary>
    /// 原子累加全局总花费（自进程启动累计，不随日 reset 清零），返回累加后的总花费。
    /// 用于 GetSpend().Session 字段（历史累计，与按 sessionId 的会话账户不同）。
    /// </summary>
    decimal AddTotal(decimal delta);

    /// <summary>
    /// 读取全局总花费，不存在则 0。
    /// </summary>
    decimal GetTotal();

    /// <summary>
    /// 重置全局总花费为 0（不触碰日/会话账户）。
    /// </summary>
    void ResetTotal();

    /// <summary>
    /// 原子累加指定会话花费，返回累加后的该会话总花费。
    /// </summary>
    /// <param name="sessionId">会话标识，非空。</param>
    /// <param name="delta">增量（USD，非负）。</param>
    /// <returns>累加后该会话总花费。</returns>
    decimal AddSession(string sessionId, decimal delta);

    /// <summary>
    /// 读取指定 UTC 日期的累计日花费，不存在则 0。
    /// </summary>
    decimal GetDaily(DateTime utcDate);

    /// <summary>
    /// 读取最近 N 天的日花费历史（含今天），按日期升序排列。
    /// </summary>
    /// <param name="days">回溯天数。</param>
    /// <returns>(日期, 花费) 列表，日期升序。</returns>
    IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days);

    /// <summary>
    /// 将当前日花费快照到历史归档。
    /// </summary>
    /// <param name="utcDate">快照日期。</param>
    void SnapshotDaily(DateTime utcDate);

    /// <summary>
    /// 读取指定会话的累计花费，会话不存在时返回 0。
    /// </summary>
    decimal GetSession(string sessionId);

    /// <summary>
    /// 重置日花费（跨 UTC 日触发）。语义上等价于清空当天及更早记录。
    /// </summary>
    void ResetDaily();

    /// <summary>
    /// 重置指定会话账户。
    /// </summary>
    void ResetSession(string sessionId);

    /// <summary>
    /// 按 updated_at 淘汰早于 <paramref name="cutoff"/> 的会话账户，防止无界增长。
    /// </summary>
    /// <param name="cutoff">UTC 时间阈值；updated_at 早于此值的会话被删除。</param>
    /// <returns>实际淘汰的会话条数。</returns>
    int EvictSessionsBefore(DateTime cutoff);

    /// <summary>
    /// 清空所有记录（日 + 全部会话）。测试与管理用。
    /// </summary>
    void ClearAll();
}
