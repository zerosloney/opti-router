using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 单模型延迟统计快照。由后台聚合服务写入，供 <see cref="LatencyAwarePolicy"/> 读。
/// </summary>
/// <param name="AverageLatencyMs">统计窗口内成功请求的平均延迟（毫秒）。</param>
/// <param name="P95LatencyMs">统计窗口内成功请求的第 95 百分位延迟（毫秒），用于压制尾部抖动。</param>
/// <param name="SampleCount">统计窗口内成功请求数。</param>
public sealed record ModelLatencyStats(double AverageLatencyMs, double P95LatencyMs, int SampleCount);

/// <summary>
/// 延迟统计读接口。路由决策层通过此接口读延迟，零 I/O、零锁。
/// </summary>
/// <remarks>
/// 后台 <c>LatencyStatsAggregatorService</c> 周期聚合 <see cref="IRequestAuditStore"/> 的审计记录，
/// 通过 <see cref="Update"/> 刷新快照。策略链只读快照，不触数据库，不阻塞请求路径。
/// </remarks>
public interface ILatencyStatsProvider
{
    /// <summary>
    /// 读取指定模型的延迟统计。模型无统计（冷启动）返回 null。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <returns>统计快照，或 null 表示无数据。</returns>
    ModelLatencyStats? GetStats(string modelName);

    /// <summary>
    /// 全量替换当前延迟快照。由后台聚合服务调用。
    /// </summary>
    /// <param name="stats">模型名到统计的映射。null 或空映射清空全部。</param>
    void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats);
}

/// <summary>
/// 延迟统计内存缓存，线程安全。读侧无锁（Volatile 引用 swap），写侧 swap 整张映射。
/// </summary>
public sealed class LatencyStatsCache : ILatencyStatsProvider
{
    private volatile IReadOnlyDictionary<string, ModelLatencyStats> _stats =
        System.Collections.Frozen.FrozenDictionary<string, ModelLatencyStats>.Empty;

    /// <inheritdoc />
    public ModelLatencyStats? GetStats(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        return _stats.TryGetValue(modelName, out var s) ? s : null;
    }

    /// <inheritdoc />
    public void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats)
    {
        // intentional-simple: 整张映射 swap，O(1) 读侧可见性。模型数通常 <50，重建成本可忽略。
        // 比逐项 ConcurrentDictionary 更新更简单，且聚合本身是低频后台操作（默认 60s 一次）。
        _stats = stats is null || stats.Count == 0
            ? System.Collections.Frozen.FrozenDictionary<string, ModelLatencyStats>.Empty
            : stats;
    }
}
