using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 策略间的共享输入，不可变。
/// </summary>
public sealed record RouterContext
{
    /// <summary>
    /// 原始请求。
    /// </summary>
    public required ChatRequest Request { get; init; }

    /// <summary>
    /// 所有已启用的模型端点。
    /// </summary>
    public required IReadOnlyList<ModelEndpointOptions> AllModels { get; init; }

    /// <summary>
    /// 路由配置。
    /// </summary>
    public required RouterOptions Options { get; init; }

    /// <summary>
    /// 估算的输入 token 数。
    /// </summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>
    /// 最近失败的模型名集合。
    /// </summary>
    public IReadOnlySet<string> FailedModels { get; init; } = new HashSet<string>();
}
