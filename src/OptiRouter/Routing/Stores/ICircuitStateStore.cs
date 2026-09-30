namespace OptiRouter.Routing;

/// <summary>
/// 熔断器状态存储抽象。
/// </summary>
public interface ICircuitStateStore
{
    /// <summary>
    /// 保存单个模型的熔断状态。
    /// </summary>
    /// <param name="modelName">模型标识。</param>
    /// <param name="state">当前熔断状态。</param>
    /// <param name="failureCount">当前连续失败计数。</param>
    /// <param name="cooldownUntil">打开状态的冷却截止 UTC 时间。</param>
    void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil);

    /// <summary>
    /// 加载所有保存的熔断状态记录。
    /// </summary>
    /// <returns>模型名到熔断状态明细的映射字典。</returns>
    Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates();
}
