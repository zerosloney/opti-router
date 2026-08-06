using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 模型熔断器状态。
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// 闭合：正常放行请求，累计连续失败。
    /// </summary>
    Closed = 0,

    /// <summary>
    /// 打开：连续失败达阈值触发熔断，请求被排除，等待冷却到期。
    /// </summary>
    Open = 1,

    /// <summary>
    /// 半开：冷却到期，允许有限的探测请求尝试恢复；探测成功则闭合，失败则重新打开。
    /// </summary>
    HalfOpen = 2
}

/// <summary>
/// 跨请求模型健康跟踪器：完整三态断路器（Closed / Open / HalfOpen）。
/// 连续失败达阈值→打开（冷却）；冷却到期→半开（放行有限探测）；
/// 探测成功→闭合，探测失败→重新打开并重新冷却。
/// </summary>
/// <remarks>
/// 线程安全，所有状态变更在单一锁内完成。
/// <para>
/// 半开探测协议：调用方在尝试模型前调用 <see cref="TryBeginProbe"/>，
/// 返回 true 后必须最终上报一次结果——成功调 <see cref="RecordSuccess"/>，
/// 失败调 <see cref="RecordFailure"/>，无法判定（如不可重试的 4xx、客户端断开）
/// 调 <see cref="ReleaseProbe"/> 仅释放探测槽位、不影响状态。
/// </para>
/// </remarks>
public sealed class ModelHealthTracker
{
    private sealed class CircuitInfo
    {
        public CircuitState State = CircuitState.Closed;
        public int FailureCount;
        public DateTime CoolDownUntil;
        public int ActiveProbes;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, CircuitInfo> _circuits = new();
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
    /// 获取模型当前熔断状态。打开状态冷却到期时惰性转入半开。未知模型返回 <see cref="CircuitState.Closed"/>。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    public CircuitState GetState(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return CircuitState.Closed;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
                return CircuitState.Closed;

            TransitionIfExpired(info);
            return info.State;
        }
    }

    /// <summary>
    /// 判定模型是否处于熔断打开（冷却）状态。半开不算冷却中——半开允许探测。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    public bool IsCoolingDown(string modelName)
    {
        return GetState(modelName) == CircuitState.Open;
    }

    /// <summary>
    /// 请求放行许可。闭合直接放行；半开占用一个探测槽位（并发探测数达上限则拒绝）；打开拒绝。
    /// 返回 true 且当时处于半开时，调用方必须最终上报结果（见类注释中的探测协议）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="maxProbes">半开状态允许的最大并发探测数。</param>
    /// <returns>true 表示允许本次请求尝试该模型。</returns>
    public bool TryBeginProbe(string modelName, int maxProbes)
    {
        if (string.IsNullOrEmpty(modelName)) return true;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
                return true; // 无记录 = 闭合

            TransitionIfExpired(info);
            switch (info.State)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    return false;

                default: // HalfOpen
                    if (info.ActiveProbes < Math.Max(maxProbes, 0))
                    {
                        info.ActiveProbes++;
                        return true;
                    }
                    return false;
            }
        }
    }

    /// <summary>
    /// 上报一次失败。闭合态累计计数、达阈值触发熔断；半开态视为探测失败、重新打开；打开态刷新冷却。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="threshold">连续失败阈值。</param>
    /// <param name="cooldownSeconds">冷却秒数。</param>
    /// <returns>true 表示本次上报后熔断处于打开状态（触发、重开或刷新）。</returns>
    public bool RecordFailure(string modelName, int threshold, int cooldownSeconds)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
            {
                info = new CircuitInfo();
                _circuits[modelName] = info;
            }

            TransitionIfExpired(info);

            // 该失败来自一次已放行的请求（可能是半开探测），先释放占位。
            if (info.ActiveProbes > 0)
                info.ActiveProbes--;

            switch (info.State)
            {
                case CircuitState.HalfOpen:
                    // 探测失败：重新打开并重新冷却。
                    info.State = CircuitState.Open;
                    info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                    info.FailureCount = 0;
                    return true;

                case CircuitState.Open:
                    // 已打开（并发在途请求的迟到失败）：刷新冷却到期时间。
                    info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                    return true;

                default: // Closed
                    info.FailureCount++;
                    if (threshold > 0 && info.FailureCount >= threshold)
                    {
                        info.State = CircuitState.Open;
                        info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                        return true;
                    }
                    return false;
            }
        }
    }

    /// <summary>
    /// 上报成功：闭合熔断器（半开探测成功即恢复），清零失败计数。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    public void RecordSuccess(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
                return;

            TransitionIfExpired(info);

            if (info.ActiveProbes > 0)
                info.ActiveProbes--;

            info.State = CircuitState.Closed;
            info.FailureCount = 0;
            info.CoolDownUntil = default;
        }
    }

    /// <summary>
    /// 仅释放一个探测槽位，不改变熔断状态。
    /// 用于无法给出健康信号的场景（不可重试错误、外部取消、客户端提前断开）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    public void ReleaseProbe(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        lock (_lock)
        {
            if (_circuits.TryGetValue(modelName, out var info) && info.ActiveProbes > 0)
                info.ActiveProbes--;
        }
    }

    /// <summary>
    /// 打开状态冷却到期时转入半开（惰性转换，无需独立定时器）。
    /// 调用方必须持有 <see cref="_lock"/>。
    /// </summary>
    private void TransitionIfExpired(CircuitInfo info)
    {
        if (info.State == CircuitState.Open && _nowProvider() >= info.CoolDownUntil)
        {
            info.State = CircuitState.HalfOpen;
            info.FailureCount = 0;
            // ActiveProbes 保留：如有在途探测，其占位继续有效。
        }
    }
}
