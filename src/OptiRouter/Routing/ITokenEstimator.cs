using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// Token 估算器抽象。给定一个请求，返回估算的输入 token 数。
/// 不同实现可在精度与开销间取舍（分桶粗估 vs 真实 BPE）。
/// </summary>
public interface ITokenEstimator
{
    /// <summary>
    /// 估算请求的输入 token 数。空消息列表返回 0。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <returns>估算的 token 数，始终非负。</returns>
    int Estimate(ChatRequest request);
}
