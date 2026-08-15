using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 显式模型固定策略（Filter 组）。
/// 客户端在请求体 <c>model</c> 字段显式指定模型时固定候选资格池：按路由名 <c>Name</c> 命中
/// 则固定该端点；否则按上游真实模型 <c>Id</c> 命中（同一模型可由多家供应商提供，固定为提供它的
/// 全部端点，路由器在其中择优与降级）。后续 tier 分类/语义路由/学习型重排只在固定池内工作，
/// 不会把请求换到未提供该模型的端点；仅硬约束（能力/长输入/数据不出域/配额/预算/熔断）可否决。
/// <c>model</c> 为空或为 <see cref="AutoAlias"/>（忽略大小写）时透传，走智能路由。
/// </summary>
public sealed class ExplicitModelPolicy : IRouterPolicy
{
    /// <summary>智能路由别名，即 /v1/models 中暴露的虚拟模型 id；也是 model 缺省时的语义。</summary>
    public const string AutoAlias = "auto";

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Filter;

    /// <summary>
    /// 判断请求的 model 字段是否表示「交给路由器智能选择」：
    /// 空/空白或 <see cref="AutoAlias"/>（忽略大小写）均为是。
    /// </summary>
    public static bool IsAutoRouting(string? requestedModel) =>
        string.IsNullOrWhiteSpace(requestedModel) ||
        string.Equals(requestedModel, AutoAlias, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var requested = context.Request.Model;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return previous.Append("explicit-model", "no model requested, smart routing");
        }

        // 统一解析：路由名 Name → 显示 ID "{供应商}/{Id}"（可带 #N 序号）→ 裸上游 Id。
        // 同一真实模型多端点（多供应商/多 Key）时固定为提供它的全部端点，路由器在其中择优/降级。
        var matches = ModelDisplayIds.Resolve(context.AllModels, requested);

        if (matches.Count > 0)
        {
            var pinned = previous with { Candidates = matches.ToList() };
            return pinned.Append("explicit-model",
                matches.Count == 1
                    ? $"pinned to '{matches[0].Name}'"
                    : $"pinned to {matches.Count} endpoints offering '{requested}'");
        }

        if (string.Equals(requested, AutoAlias, StringComparison.OrdinalIgnoreCase))
        {
            return previous.Append("explicit-model", "auto alias, smart routing");
        }

        // 未知模型名：端点层已按 404 拒绝；策略侧防御性透传，不清空候选。
        return previous.Append("explicit-model", $"unknown model '{requested}', passthrough");
    }
}
