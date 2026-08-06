using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 跨请求模型健康跟踪器（简单熔断）：连续失败达阈值→冷却；冷却到期自动恢复；成功清零计数。
/// </summary>
/// <remarks>
/// 线程安全，lock 模式仿 <see cref="CostLedger"/>。
/// intentional-simple: 仅冷却计时，无 HalfOpen 探测。冷却到期直接放行，
/// 若仍失败会再次累计并重新冷却。对路由降级足够，复杂场景可升级为完整断路器。
/// </remarks>
public sealed class ModelHealthTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _failureCounts = new();
    private readonly Dictionary<string, DateTime> _coolDownUntil = new();
    private readonly Func<DateTime> _nowProvider;

    /// <summary>
    /// 用系统 UTC 时钟构造。
    /// </summary>
    public ModelHealthTracker() : this(() => DateTime.UtcNow) { }

    /// <summary>
    /// 用自定义时钟构造（测试可注入）。
    /// </summary>
    /// <param name="nowProvider">返回当前 UTC 时间。</param>
    public ModelHealthTracker(Func<DateTime> nowProvider)
    {
        _nowProvider = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));
    }

    /// <summary>
    /// 上报一次失败。达到阈值时记录冷却到期时间。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="threshold">连续失败阈值。</param>
    /// <param name="cooldownSeconds">冷却秒数。</param>
    /// <returns>true 表示本次上报触发了熔断（进入冷却）。</returns>
    public bool RecordFailure(string modelName, int threshold, int cooldownSeconds)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        lock (_lock)
        {
            if (!_failureCounts.TryGetValue(modelName, out int count))
                count = 0;
            count++;
            _failureCounts[modelName] = count;

            if (count >= threshold && threshold > 0)
            {
                _coolDownUntil[modelName] = _nowProvider().AddSeconds(cooldownSeconds);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 上报成功，清零该模型的失败计数并移除冷却。
    /// </summary>
    public void RecordSuccess(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        lock (_lock)
        {
            _failureCounts.Remove(modelName);
            _coolDownUntil.Remove(modelName);
        }
    }

    /// <summary>
    /// 判定模型是否处于冷却中。
    /// </summary>
    public bool IsCoolingDown(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        lock (_lock)
        {
            if (!_coolDownUntil.TryGetValue(modelName, out DateTime until)) return false;
            if (_nowProvider() < until) return true;

            // 冷却到期，清理（懒清理，无需独立定时器）。
            _coolDownUntil.Remove(modelName);
            _failureCounts.Remove(modelName);
            return false;
        }
    }
}
