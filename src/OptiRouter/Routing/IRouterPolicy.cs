using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 路由策略抽象。每个策略接收上下文和前一步决策，返回调整后的决策。
/// </summary>
public interface IRouterPolicy
{
    /// <summary>
    /// 应用策略，可能调整候选链。
    /// </summary>
    RouterDecision Apply(RouterContext context, RouterDecision previous);
}
