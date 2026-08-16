namespace OptiRouter.Concurrency;

/// <summary>
/// 基于 TCP Vegas / AIMD 算法的上游模型自适应并发拥塞控制器。
/// 针对上游 API 延迟飙升（拥塞）动态收缩并发许可，防止内存与线程池暴涨。
/// </summary>
public interface IAdaptiveConcurrencyLimiter
{
    /// <summary>
    /// 获取针对特定模型的自适应并发许可。调用返回的 IDisposable 可通过 using 自动释放。
    /// </summary>
    Task<IDisposable> AcquireAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 汇报上游请求 RTT 往返延迟（毫秒），驱动 AIMD 窗口收缩与扩张。
    /// </summary>
    void RecordRtt(string modelName, double rttMs);

    /// <summary>
    /// 获取当前针对指定模型的实时并发许可上限。
    /// </summary>
    int GetCurrentLimit(string modelName);
}
