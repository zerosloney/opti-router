using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 路由决策结果，贯穿策略链。
/// </summary>
public sealed record RouterDecision
{
    /// <summary>
    /// 选中的候选模型列表，按优先级排序。第一个是首选，其余是降级备用。
    /// </summary>
    public required IReadOnlyList<ModelEndpointOptions> Candidates { get; init; }

    /// <summary>
    /// 首选模型（Candidates[0]）。
    /// </summary>
    public ModelEndpointOptions Primary => Candidates[0];

    /// <summary>
    /// 决策理由（人类可读，用于日志/调试）。
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// 估算的输入 token 数。
    /// </summary>
    public int EstimatedInputTokens { get; init; }
}
