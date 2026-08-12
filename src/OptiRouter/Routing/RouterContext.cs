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
    /// 当前策略阶段仍符合资格的模型端点。
    /// 初始值为所有已启用模型；RouterEngine 在 Filter 组中逐步收窄该池，
    /// 后续策略只能从最终资格池产生候选。
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

    /// <summary>
    /// 可选会话 ID（来自 X-Session-Id 头）。null 表示无会话，会话预算不生效。
    /// </summary>
    public string? SessionId { get; init; }
}
