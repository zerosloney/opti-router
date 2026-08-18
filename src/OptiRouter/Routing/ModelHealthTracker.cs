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
        public int HalfOpenSuccesses;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, CircuitInfo> _circuits = new();
    private readonly Func<DateTime> _nowProvider;
    private readonly ICircuitStateStore? _store;

    /// <summary>
    /// 用系统 UTC 时钟构造。
    /// </summary>
    public ModelHealthTracker() : this(null, () => DateTime.UtcNow) { }

    /// <summary>
    /// 用存储器和系统时钟构造。
    /// </summary>
    public ModelHealthTracker(ICircuitStateStore? store) : this(store, () => DateTime.UtcNow) { }

    /// <summary>
    /// 用自定义时钟构造（测试可注入）。
    /// </summary>
    /// <param name="nowProvider">返回当前 UTC 时间。</param>
    public ModelHealthTracker(Func<DateTime> nowProvider) : this(null, nowProvider) { }

    /// <summary>
    /// 用存储器和自定义时钟构造。
    /// </summary>
    public ModelHealthTracker(ICircuitStateStore? store, Func<DateTime> nowProvider)
    {
        _store = store;
        _nowProvider = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));

        if (_store != null)
        {
            var loaded = _store.LoadCircuitStates();
            foreach (var kvp in loaded)
            {
                _circuits[kvp.Key] = new CircuitInfo
                {
                    State = kvp.Value.State,
                    FailureCount = kvp.Value.FailureCount,
                    CoolDownUntil = kvp.Value.CooldownUntil
                };
            }
        }
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

            TransitionIfExpired(modelName, info);
            return info.State;
        }
    }

    /// <summary>
    /// 获取当前所有处于非闭合状态（或有失败记录）的模型熔断状态快照。
    /// </summary>
    public IReadOnlyDictionary<string, (CircuitState State, int FailureCount, int ActiveProbes)> GetCircuitsSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new Dictionary<string, (CircuitState State, int FailureCount, int ActiveProbes)>(StringComparer.Ordinal);
            foreach (var kvp in _circuits)
            {
                TransitionIfExpired(kvp.Key, kvp.Value);
                snapshot[kvp.Key] = (kvp.Value.State, kvp.Value.FailureCount, kvp.Value.ActiveProbes);
            }
            return snapshot;
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

            TransitionIfExpired(modelName, info);
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
    /// <param name="releaseProbe">
    /// 是否顺带释放一个半开探测槽位。默认 true：调用方经 <see cref="TryBeginProbe"/> 放行、
    /// 持有槽位（主链候选 / Fusion panel / Race）。未经放行的旁路健康信号
    /// （Fusion analyst/outer、Cascade verifier/strong、后台探活服务）必须传 false——
    /// 否则会偷走在途真实探测的槽位递减，使 halfOpenMaxProbes 并发上限失效。
    /// </param>
    /// <returns>true 表示本次上报后熔断处于打开状态（触发、重开或刷新）。</returns>
    public bool RecordFailure(string modelName, int threshold, int cooldownSeconds, bool releaseProbe = true)
    {
        if (string.IsNullOrEmpty(modelName)) return false;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
            {
                info = new CircuitInfo();
                _circuits[modelName] = info;
            }

            TransitionIfExpired(modelName, info);

            // 该失败来自一次已放行的请求（可能是半开探测），先释放占位。
            if (releaseProbe && info.ActiveProbes > 0)
                info.ActiveProbes--;

            bool result = false;
            switch (info.State)
            {
                case CircuitState.HalfOpen:
                    // 探测失败：重新打开并重新冷却，清零半开成功计数。
                    info.State = CircuitState.Open;
                    info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                    info.FailureCount = 0;
                    info.HalfOpenSuccesses = 0;
                    result = true;
                    break;

                case CircuitState.Open:
                    // 已打开（并发在途请求的迟到失败）：刷新冷却到期时间。
                    info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                    result = true;
                    break;

                default: // Closed
                    info.FailureCount++;
                    if (threshold > 0 && info.FailureCount >= threshold)
                    {
                        info.State = CircuitState.Open;
                        info.CoolDownUntil = _nowProvider().AddSeconds(cooldownSeconds);
                        info.HalfOpenSuccesses = 0;
                        result = true;
                    }
                    break;
            }

            _store?.SaveCircuitState(modelName, info.State, info.FailureCount, info.CoolDownUntil);
            return result;
        }
    }

    /// <summary>
    /// 上报成功。闭合态直接清零；半开态累计连续探测成功，达 <paramref name="requiredSuccesses"/> 才闭合，
    /// 未达阈值保持半开（释放探测槽位让下一轮探测进入）；任一失败（见 <see cref="RecordFailure"/>）重开并清零。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="requiredSuccesses">半开态连续成功闭合阈值；默认 1（单次成功即恢复，保持旧行为）。</param>
    /// <param name="releaseProbe">是否顺带释放一个半开探测槽位；语义同 <see cref="RecordFailure"/> 的同名参数。</param>
    public void RecordSuccess(string modelName, int requiredSuccesses = 1, bool releaseProbe = true)
    {
        if (string.IsNullOrEmpty(modelName)) return;
        int threshold = Math.Max(1, requiredSuccesses);

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
                return;

            TransitionIfExpired(modelName, info);

            if (releaseProbe && info.ActiveProbes > 0)
                info.ActiveProbes--;

            if (info.State == CircuitState.HalfOpen)
            {
                info.HalfOpenSuccesses++;
                if (info.HalfOpenSuccesses < threshold)
                {
                    // 未达闭合阈值：保持半开，等下一轮探测。槽位已释放。
                    _store?.SaveCircuitState(modelName, info.State, info.FailureCount, info.CoolDownUntil);
                    return;
                }
            }

            // 闭合态成功 / 半开累计达标：闭合并清零。
            info.State = CircuitState.Closed;
            info.FailureCount = 0;
            info.HalfOpenSuccesses = 0;
            info.CoolDownUntil = default;
            _store?.SaveCircuitState(modelName, info.State, info.FailureCount, info.CoolDownUntil);
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
    /// 手动干预断路器状态：强行将模型断路器重置为指定状态（如闭合正常或强制隔离打开）。
    /// </summary>
    public void ForceSetState(string modelName, CircuitState newState)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        lock (_lock)
        {
            if (!_circuits.TryGetValue(modelName, out var info))
            {
                info = new CircuitInfo();
                _circuits[modelName] = info;
            }

            info.State = newState;
            info.FailureCount = 0;
            info.ActiveProbes = 0;
            info.HalfOpenSuccesses = 0;
            info.CoolDownUntil = newState == CircuitState.Open ? _nowProvider().AddHours(1) : default;
            _store?.SaveCircuitState(modelName, info.State, info.FailureCount, info.CoolDownUntil);
        }
    }

    /// <summary>
    /// 打开状态冷却到期时转入半开（惰性转换，无需独立定时器）。
    /// 调用方必须持有 <see cref="_lock"/>。
    /// </summary>
    private void TransitionIfExpired(string modelName, CircuitInfo info)
    {
        if (info.State == CircuitState.Open && _nowProvider() >= info.CoolDownUntil)
        {
            info.State = CircuitState.HalfOpen;
            info.FailureCount = 0;
            _store?.SaveCircuitState(modelName, info.State, info.FailureCount, info.CoolDownUntil);
            // ActiveProbes 保留：如有在途探测，其占位继续有效。
        }
    }
}
