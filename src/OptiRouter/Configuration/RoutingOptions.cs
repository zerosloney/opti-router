namespace OptiRouter.Configuration;

/// <summary>
/// 路由策略开关与参数。
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>
    /// 是否启用规则分类器。
    /// </summary>
    public bool EnableRuleClassifier { get; set; } = true;

    /// <summary>
    /// 是否启用 token 估算器。
    /// </summary>
    public bool EnableTokenEstimator { get; set; } = true;

    /// <summary>
    /// 是否启用预算守卫。
    /// </summary>
    public bool EnableBudgetGuard { get; set; } = true;

    /// <summary>
    /// 是否启用故障转移。
    /// </summary>
    public bool EnableFailover { get; set; } = true;

    /// <summary>
    /// 长输入阈值（token 数）。超过此值时优先路由到大上下文模型。
    /// </summary>
    public int LongInputThresholdTokens { get; set; } = 32000;

    /// <summary>
    /// 默认能力分档。无明确信号时选哪档模型。
    /// </summary>
    public ModelTier DefaultTier { get; set; } = ModelTier.Medium;

    /// <summary>
    /// 触发跨请求熔断的连续失败次数阈值。达到后该模型进入冷却。
    /// </summary>
    public int FailoverFailureThreshold { get; set; } = 3;

    /// <summary>
    /// 熔断冷却时长（秒）。冷却到期后模型自动重新进入候选。
    /// </summary>
    public int FailoverCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Token 估算模式。默认 <see cref="TokenEstimationMode.Tiktoken"/>（真实 BPE 精确计数，异常回退分桶粗估）。
    /// </summary>
    public TokenEstimationMode TokenEstimation { get; set; } = TokenEstimationMode.Tiktoken;

    /// <summary>
    /// Tiktoken 编码名称，仅当 <see cref="TokenEstimation"/> 为 <see cref="TokenEstimationMode.Tiktoken"/> 时生效。
    /// 常见取值：<c>o200k_base</c>（GPT-4o 系）、<c>cl100k_base</c>（GPT-4/3.5 系）。
    /// </summary>
    public string TiktokenEncoding { get; set; } = "o200k_base";

    /// <summary>
    /// 半开（HalfOpen）状态下允许并发探测的最大请求数。
    /// 冷却到期进入半开后，最多放行这么多请求作为探测：探测成功则闭合熔断，失败则重新冷却。
    /// 必须大于 0。
    /// </summary>
    public int FailoverHalfOpenMaxProbes { get; set; } = 1;
}
