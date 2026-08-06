using System.Collections.ObjectModel;

namespace OptiRouter.Configuration;

/// <summary>
/// 根配置，包含模型端点、预算与路由策略。
/// </summary>
public sealed class RouterOptions
{
    /// <summary>
    /// 已配置的模型端点列表。
    /// </summary>
    public IList<ModelEndpointOptions> Models { get; } = new Collection<ModelEndpointOptions>();

    /// <summary>
    /// 成本预算相关配置。
    /// </summary>
    public BudgetOptions Budget { get; set; } = new BudgetOptions();

    /// <summary>
    /// 路由策略开关与参数。
    /// </summary>
    public RoutingOptions Routing { get; set; } = new RoutingOptions();
}
