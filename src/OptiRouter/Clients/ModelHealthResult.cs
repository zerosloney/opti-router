namespace OptiRouter.Clients;

/// <summary>
/// 模型端点健康探测结果。
/// </summary>
/// <param name="Healthy">是否健康。</param>
/// <param name="LatencyMs">探测延迟（毫秒）。</param>
/// <param name="Error">失败时的错误描述，成功时为 null。</param>
/// <param name="StatusCode">上游 HTTP 状态码（若可用）。</param>
/// <param name="Metadata">规范化响应元数据（若可用）。</param>
/// <param name="Reply">探活问题（"你是什么模型"）的回答文本，供管理台核对模型身份；无文本时为 null。</param>
public sealed record ModelHealthResult(
    bool Healthy,
    int LatencyMs,
    string? Error = null,
    System.Net.HttpStatusCode? StatusCode = null,
    UpstreamResponseMetadata? Metadata = null,
    string? Reply = null);
