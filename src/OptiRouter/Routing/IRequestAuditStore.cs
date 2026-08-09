using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 请求审计记录存储抽象。
/// </summary>
public interface IRequestAuditStore : IDisposable
{
    /// <summary>
    /// 追加一条审计记录。
    /// </summary>
    void Append(RequestAuditRecord record);

    /// <summary>
    /// 读取最近的审计记录（按时间倒序）。
    /// </summary>
    /// <param name="limit">最大返回条数。</param>
    IReadOnlyList<RequestAuditRecord> GetRecent(int limit);

    /// <summary>
    /// 按模型名筛选最近的审计记录。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="limit">最大返回条数。</param>
    IReadOnlyList<RequestAuditRecord> GetByModel(string modelName, int limit);

    /// <summary>
    /// 按时间范围分页查询审计记录（按时间倒序）。
    /// </summary>
    /// <param name="from">起始 UTC 时间（含）。</param>
    /// <param name="to">结束 UTC 时间（含）。</param>
    /// <param name="limit">本页条数。</param>
    /// <param name="offset">跳过条数。</param>
    (IReadOnlyList<RequestAuditRecord> Items, int TotalCount) GetByTimeRange(DateTime from, DateTime to, int limit, int offset);

    /// <summary>
    /// 按时间范围聚合失败统计：返回 (失败数, 总数)。供 AlertEngine 失败率检查用，
    /// 替代 GetByTimeRange(int.MaxValue) 全量物化——O(1) 内存、单条聚合查询。
    /// </summary>
    /// <param name="from">起始 UTC 时间（含）。</param>
    /// <param name="to">结束 UTC 时间（含）。</param>
    /// <returns>(失败请求数, 总请求数)，失败 = success=0。</returns>
    (int Failures, int Total) GetFailureStats(DateTime from, DateTime to);

    /// <summary>
    /// 淘汰早于指定时间的审计记录，返回实际淘汰条数。
    /// </summary>
    int EvictBefore(DateTime cutoff);

    /// <summary>
    /// 按模型聚合指定时间以来的成功请求延迟统计。
    /// 供 <see cref="ILatencyStatsProvider"/> 后台聚合用——失败/重试请求不统计（污染延迟分布）。
    /// </summary>
    /// <param name="since">统计起始 UTC 时间（含）。</param>
    /// <returns>模型名到 (平均延迟ms, 样本数) 的映射。</returns>
    IReadOnlyDictionary<string, (double AverageLatencyMs, int SampleCount)> GetLatencyStatsSince(DateTime since);
}
