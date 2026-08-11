namespace OptiRouter.Routing;

/// <summary>
/// 策略分组契约。策略链按组依赖序执行（Filter→Classify→Order→Constraint），
/// 组内保留串行（叠加过滤/fallback/重排语义）。分组是未来并行化的地基。
/// </summary>
public enum PolicyGroup
{
    /// <summary>过滤型：排除不满足条件的模型（CapabilityFilter/LongInput/Failover/QuotaAware）。</summary>
    Filter,

    /// <summary>分类型：推断目标 tier / 语义覆盖（RuleClassifier/SemanticRouter）。</summary>
    Classify,

    /// <summary>排序型：段内重排（LatencyAware/PromptCacheAffinity）。</summary>
    Order,

    /// <summary>约束型：预算/会话/负载约束（BudgetGuard/SessionAffinity/LoadBalance）。</summary>
    Constraint
}

/// <summary>
/// 路由策略抽象。每个策略接收上下文和前一步决策，返回调整后的决策。
/// </summary>
public interface IRouterPolicy
{
    /// <summary>
    /// 策略所属分组。决定策略链中的执行阶段（Filter→Classify→Order→Constraint）。
    /// </summary>
    PolicyGroup Group { get; }

    /// <summary>
    /// 应用策略，可能调整候选链。
    /// </summary>
    RouterDecision Apply(RouterContext context, RouterDecision previous);
}
