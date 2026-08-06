namespace OptiRouter.Clients;

/// <summary>
/// 模型端点健康探测结果。
/// </summary>
/// <param name="Healthy">是否健康。</param>
/// <param name="LatencyMs">探测延迟（毫秒）。</param>
/// <param name="Error">失败时的错误描述，成功时为 null。</param>
public sealed record ModelHealthResult(bool Healthy, int LatencyMs, string? Error = null);
