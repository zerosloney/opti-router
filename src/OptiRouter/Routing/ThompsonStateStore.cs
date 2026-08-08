using System;
using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 线程安全内存状态存储。维护每个模型端点的 Alpha 和 Beta 自适应多臂老虎机参数。
/// </summary>
public sealed class ThompsonStateStore
{
    /// <summary>
    /// 模型在 Thompson 采样下的统计参数。
    /// </summary>
    public sealed class ModelStats
    {
        /// <summary>
        /// Alpha（“良好”响应虚拟次数）。初始定为 1.0 建立 Beta(1,1) 的均匀分布。
        /// </summary>
        public double Alpha { get; set; } = 1.0;

        /// <summary>
        /// Beta（“不佳”响应虚拟次数）。初始定为 1.0。
        /// </summary>
        public double Beta { get; set; } = 1.0;

        /// <summary>
        /// 独占锁，保证多线程更新的原子性和一致性。
        /// </summary>
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, ModelStats> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或添加指定模型的采样指标参数。
    /// </summary>
    public ModelStats GetOrAdd(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return _states.GetOrAdd(modelName, _ => new ModelStats());
    }

    /// <summary>
    /// 记录一次端点请求表现。
    /// </summary>
    /// <param name="modelName">模型唯一标识。</param>
    /// <param name="isGood">延迟满足设定指标且请求成功时为 true，反之为 false。</param>
    /// <param name="discountFactor">指数衰减折扣因子（如 0.95），用于淡化历史贡献，应对非平稳环境。</param>
    public void RecordOutcome(string modelName, bool isGood, double discountFactor)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        double factor = Math.Clamp(discountFactor, 0.1, 1.0);
        var stats = GetOrAdd(modelName);

        lock (stats.Lock)
        {
            stats.Alpha = stats.Alpha * factor + (isGood ? 1.0 : 0.0);
            stats.Beta = stats.Beta * factor + (isGood ? 0.0 : 1.0);
        }
    }
}
