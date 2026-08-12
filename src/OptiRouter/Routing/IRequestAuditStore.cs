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
    /// 按时间范围聚合多维度统计（请求数/失败/输入输出 token/缓存命中/延迟/成本），
    /// 供 Dashboard 多窗口统计用。单条聚合查询，O(1) 内存。
    /// </summary>
    /// <param name="from">起始 UTC 时间（含）。传 <see cref="DateTime.MinValue"/> 表示无下界（"全部"窗口）。</param>
    /// <param name="to">结束 UTC 时间（含）。</param>
    /// <returns>窗口内聚合统计。</returns>
    WindowAggregateStats GetAggregateStats(DateTime from, DateTime to);

    /// <summary>
    /// 淘汰早于指定时间的审计记录，返回实际淘汰条数。
    /// </summary>
    int EvictBefore(DateTime cutoff);

    /// <summary>
    /// 按模型聚合指定时间以来的成功请求延迟统计。
    /// 供 <see cref="ILatencyStatsProvider"/> 后台聚合用——失败/重试请求不统计（污染延迟分布）。
    /// </summary>
    /// <param name="since">统计起始 UTC 时间（含）。</param>
    /// <returns>模型名到延迟统计（平均/p95/样本数）的映射。</returns>
    IReadOnlyDictionary<string, ModelLatencyStats> GetLatencyStatsSince(DateTime since);
}

/// <summary>
/// 时间窗口内的多维度聚合统计，供 Dashboard 多窗口展示。聚合 sum 用 long 避免 token 累计溢出。
/// </summary>
/// <param name="TotalRequests">窗口内请求总数。</param>
/// <param name="Failures">失败请求数（success=0）。</param>
/// <param name="InputTokens">输入 token 合计（prompt_tokens 之和）。</param>
/// <param name="OutputTokens">输出 token 合计（completion_tokens 之和）。</param>
/// <param name="CachedInputTokens">缓存命中输入 token 合计。</param>
/// <param name="CacheWriteInputTokens">缓存写入输入 token 合计。</param>
/// <param name="UncachedInputTokens">未缓存输入 token 合计。</param>
/// <param name="SuccessLatencySumMs">成功请求延迟合计（毫秒）。失败/重试不计入以免污染分布。</param>
/// <param name="SuccessLatencySamples">成功请求样本数。</param>
/// <param name="TotalCost">窗口内成本合计（USD）。</param>
public sealed record WindowAggregateStats(
    int TotalRequests,
    int Failures,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long UncachedInputTokens,
    long SuccessLatencySumMs,
    int SuccessLatencySamples,
    double TotalCost);
