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
    /// 首选模型（Candidates[0]）。调用方须确保候选非空（决策链保证有候选，空候选即失败态）。
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

    /// <summary>是否为流式请求。作为在线学习的稳定、无 I/O 上下文特征。</summary>
    public bool RequestIsStreaming { get; init; }

    /// <summary>请求消息数。作为在线学习的多轮上下文特征。</summary>
    public int RequestMessageCount { get; init; }

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

    /// <summary>
    /// 本次路由决策中被 ε 探索提升到段首的模型名（决策级信息，同决策的所有尝试行共享同一值）。
    /// null = 无探索提升。由 <c>LatencyAwarePolicy</c> 填充。
    /// </summary>
    public string? EpsilonPromotedModel { get; init; }

    /// <summary>
    /// 请求文本的 CJK 字符占比 [0,1]。由 <c>RouterEngine</c> 填充。
    /// </summary>
    public double CjkRatio { get; init; }

    /// <summary>
    /// 请求的最大生成 token 数。由 <c>RouterEngine</c> 填充。
    /// </summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// 请求是否携带工具调用。由 <c>RouterEngine</c> 填充。
    /// </summary>
    public bool HasTools { get; init; }
    
    /// <summary>
    /// 本次路由请求选定的预设模式：Cost (省钱) / Balanced (平衡) / Intelligence (质量)。
    /// null = 默认或未指定模式。
    /// </summary>
    public RoutingMode? RoutingMode { get; init; }
    
    /// <summary>
    /// 路由决定最终锚定的目标档位。
    /// 若请求显式 pin 模型，则为该模型的档位；若走 auto 模式，则为模式对应的预设档位。
    /// </summary>
    public ModelTier? TargetTier { get; init; }

    /// <summary>
    /// 本请求被硬约束（数据主权/能力过滤）排除、全灭补链也不可用的模型名。
    /// 硬约束 Filter 策略排除时留痕；FailoverPolicy 补链原料据此过滤——
    /// 补链可以越过 tier 偏好/pin 等软过滤（全灭逃生门），但不能绕过合规与能力正确性。
    /// </summary>
    public IReadOnlyList<string> HardExcludedModels { get; init; } = Array.Empty<string>();
}    
