using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 结构化决策事件：策略名 + 详情。供机器解析（P1 分类信号、P3 上下文特征、未来可观测）。
/// </summary>
public sealed record ReasonEvent(string Policy, string Detail);

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
    /// 预算耗尽信号：由 <c>BudgetGuardPolicy</c> 在 Reject 模式下置 true。
    /// <see cref="Endpoints.ProxyOrchestrator"/> 据此抛 <c>BudgetExhaustedException</c>，
    /// 不依赖对 <see cref="Reason"/> 字符串匹配（脆弱契约）。
    /// </summary>
    public bool BudgetExhausted { get; init; }

    /// <summary>
    /// 估算的输入 token 数。
    /// </summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>
    /// Structured complexity signal. Behavioral consumers must use this field and
    /// never parse <see cref="Reason"/>.
    /// </summary>
    public RequestComplexity RequestComplexity { get; init; } = RequestComplexity.Unknown;

    /// <summary>
    /// 分类信号（如 "code-complex" / "simple-qa"）。由 <c>RuleClassifierPolicy</c> 填充，
    /// 供生产端直接读取（不解析 <see cref="Reason"/> 字符串）。null = 未分类/未启用。
    /// </summary>
    public string? ClassificationSignal { get; init; }

    /// <summary>
    /// 分类信号应路由的目标 tier。与 <see cref="ClassificationSignal"/> 配套，
    /// 供生产端直接读取。null = 未分类/未启用。
    /// </summary>
    public ModelTier? ClassificationTargetTier { get; init; }

    /// <summary>
    /// 结构化决策事件列表（机器可解析）。各策略在拼接 <see cref="Reason"/> 字符串之外追加。
    /// <see cref="Reason"/> 保持人类可读拼接（向后兼容日志/审计/测试断言）。
    /// </summary>
    public IReadOnlyList<ReasonEvent> ReasonEvents { get; init; } = Array.Empty<ReasonEvent>();
}
