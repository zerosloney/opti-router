using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 模型能力分档排序的单一真源。
/// </summary>
/// <remarks>
/// RouterEngine 初始候选构造与 FailoverPolicy.BuildFallbackChain 都依赖 tier 优先序。
/// 抽出此处避免两处对 tier 顺序的假设不一致（一处用 (int)枚举序，一处用显式数组），
/// 改枚举值时只需调这里。
/// </remarks>
public static class TierOrder
{
    /// <summary>
    /// tier 的优先级排序值，越小越优先（能力越强）。Strong=0, Medium=1, Cheap=2。
    /// 与 <see cref="ModelTier"/> 枚举整数值刻意保持一致，但本表是显式真源——
    /// 若未来调整优先序，只改此方法，不依赖枚举整数。
    /// </summary>
    public static int Rank(ModelTier tier) => tier switch
    {
        ModelTier.Strong => 0,
        ModelTier.Medium => 1,
        ModelTier.Cheap => 2,
        _ => 99
    };

    /// <summary>
    /// 降级链顺序：Strong → Medium → Cheap。供 fallback 逐档尝试。
    /// </summary>
    public static readonly ModelTier[] FallbackChain =
    {
        ModelTier.Strong,
        ModelTier.Medium,
        ModelTier.Cheap
    };
}
