using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 分桶加权粗估实现：委托给静态 <see cref="TokenEstimator"/>。
/// 零依赖、极快，误差约 ±15%，作为 Tiktoken 模式异常时的回退。
/// </summary>
public sealed class BucketTokenEstimator : ITokenEstimator
{
    /// <inheritdoc />
    public int Estimate(ChatRequest request)
    {
        return TokenEstimator.Estimate(request);
    }
}
