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

    /// <summary>
    /// 半开态连续探测成功多少次后才闭合熔断。默认 1（单次成功即恢复，保持既有行为）。
    /// 调高（如 3）可防止单次偶然成功导致抖动——慢恢复模型需连续多次探测成功才视为稳定。
    /// 必须大于 0。
    /// </summary>
    public int FailoverHalfOpenRequiredSuccesses { get; set; } = 1;

    /// <summary>
    /// 流式响应（SSE）的最大允许累积字节数（保护性硬限制，防止 OOM/恶意无限流输出）。
    /// 默认 20MB。
    /// </summary>
    public long MaxResponseStreamBytes { get; set; } = 20 * 1024 * 1024; // 20 MB

    /// <summary>
    /// 是否启用后台主动健康探活（定时对所有启用模型发探测请求，结果上报断路器）。
    /// 默认 true。关闭则熔断恢复纯靠真实流量半开探测。
    /// </summary>
    public bool EnableHealthProbe { get; set; } = true;

    /// <summary>
    /// 后台探活间隔秒数。默认 60。最小 10（低于 10 按 10 计）。
    /// </summary>
    public int HealthProbeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 是否启用向量空间语义路由器。
    /// </summary>
    public bool EnableSemanticRouter { get; set; } = true;

    /// <summary>
    /// 向量余弦相似度匹配阈值。取值范围 [0.0, 1.0]。默认 0.25。
    /// </summary>
    public double SemanticSimilarityThreshold { get; set; } = 0.25;

    /// <summary>
    /// 语义路由规则列表。
    /// </summary>
    public System.Collections.Generic.List<SemanticRouteOptions> SemanticRoutes { get; set; } = new();

    /// <summary>
    /// 是否启用会话粘性路由。开启后，同 X-Session-Id 的多轮对话尽量命中上次成功使用的模型，
    /// 避免每轮重新路由导致风格/能力割裂。默认 false。
    /// 粘性模型若已被失败排除或熔断，下游策略（Failover/LongInput）仍会覆盖，保证可用性。
    /// </summary>
    public bool EnableSessionAffinity { get; set; } = false;

    /// <summary>
    /// 会话粘性记录的存活时长（秒）。默认 600（10 分钟）。
    /// 超时后该会话下次请求重新路由，避免长期固定在已不合适的模型。
    /// </summary>
    public int SessionAffinityTtlSeconds { get; set; } = 600;

    /// <summary>
    /// 是否启用同 tier 负载均衡。开启后，候选链中同一 tier 段内的模型按 MaxContextTokens 加权随机重排，
    /// 避免同 tier 首模型承担全部流量。跨 tier 顺序不变（保留能力择优）。默认 false。
    /// 放在策略链末（Failover 之后），仅对熔断排除后的存活候选生效。
    /// </summary>
    public bool EnableLoadBalance { get; set; } = false;

    /// <summary>
    /// 是否启用 Cheap→Strong 级联自校验。开启后，路由到 Cheap 模型的请求（仅非流式）按采样率做一次
    /// 自校验（同 Cheap 模型判定 CONFIDENT/UNCERTAIN），低置信则升级 Strong 模型重答。
    /// 闭质量漏洞：规则误判简单→Cheap 答错时仍有兜底。默认 false。
    /// 流式请求不级联（首 chunk 已透传无法切模型）。
    /// </summary>
    public bool EnableCascadeUpgrade { get; set; } = false;

    /// <summary>
    /// 级联自校验采样率 [0.0, 1.0]。仅采样的 Cheap 请求触发自校验，防全量升级成本爆炸。默认 0.1（10%）。
    /// 0.0 = 完全关闭级联（即使 EnableCascadeUpgrade=true）；1.0 = 全部 Cheap 请求都校验。
    /// </summary>
    public double CascadeUpgradeSampleRate { get; set; } = 0.1;

    /// <summary>
    /// 级联自校验用的复核 prompt。模型应只回 CONFIDENT / UNCERTAIN。
    /// 留空则用内置默认 prompt（中文）。
    /// </summary>
    public string CascadeUpgradeSelfVerifyPrompt { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用延迟感知路由。开启后，同 tier 段内按历史平均延迟重排（快模型优先），
    /// 跨 tier 顺序不变。延迟统计由后台 <c>LatencyStatsAggregatorService</c> 聚合，决策层零 I/O。
    /// 冷启动（样本不足）时透传，退回 MaxContextTokens 排序。默认 false。
    /// </summary>
    public bool EnableLatencyAware { get; set; } = false;

    /// <summary>
    /// 延迟感知生效所需的最小样本数。模型历史成功请求数低于此值时不参与延迟排序（噪声大）。
    /// 默认 10。设为 0 则忽略样本数检查（不推荐，冷启动噪声会污染排序）。
    /// </summary>
    public int LatencyMinSamples { get; set; } = 10;

    /// <summary>
    /// 延迟聚合统计窗口（分钟）。后台聚合只统计此窗口内的成功请求延迟。
    /// 默认 60。窗口越长越平滑，但响应慢（模型变慢后需等窗口滚动才反映）。
    /// 必须 > 0。
    /// </summary>
    public int LatencyStatsWindowMinutes { get; set; } = 60;

    /// <summary>
    /// 是否启用模型能力过滤。开启后，根据请求内容检测所需能力（vision/tool-use/json-mode），
    /// 排除 Tags 不含所需能力的模型。无能力需求时透传，过滤后为空时保留原候选（让上游报错）。
    /// 能力标注通过 <see cref="ModelEndpointOptions.Tags"/> 表达，语义约定：
    /// "vision" / "tool-use" / "json-mode"。默认 false。
    /// </summary>
    public bool EnableCapabilityFilter { get; set; } = false;

    /// <summary>
    /// 是否启用并行首试（Fusion-lite）。开启后，非流式请求首轮并行尝试候选链前 N 个模型，
    /// 取最快成功响应，取消其余。仅非流式（流式首 chunk 锁定模型无法切换）。
    /// 成本语义：所有并行尝试的真实消耗都入账（上游对已发出的请求仍计费）。
    /// 审计语义：每个尝试记一条，共享 ParallelGroupId，仅采纳的标记 IsAdopted=true。
    /// 默认 false。与 EnableFailover 正交（熔断排除仍生效）。
    /// </summary>
    public bool EnableFusionMode { get; set; } = false;

    /// <summary>
    /// 并行首试首轮并发数。默认 2，范围 [2, 5]。值越大延迟越低但成本越高（并发 token 消耗）。
    /// 半开模型探测槽位满时自动降级为串行单独尝试。
    /// </summary>
    public int FusionMaxParallel { get; set; } = 2;

    /// <summary>
    /// 是否启用多维能力评估路由。
    /// </summary>
    public bool EnableMultiDimensionalRouting { get; set; } = false;

    /// <summary>
    /// 是否启用 Thompson 采样自适应延迟感知路由。
    /// </summary>
    public bool EnableThompsonSampling { get; set; } = false;

    /// <summary>
    /// Thompson 采样的历史折扣/衰减因子（0.5 至 0.99）。值越小，系统对端点性能变化的反应越灵敏。
    /// </summary>
    public double ThompsonDiscountFactor { get; set; } = 0.95;

    /// <summary>
    /// 理想平均延迟目标（毫秒）。实际成功延迟小于该值计为 Alpha 自适应成功增量，否则（超时、大延迟或故障）计为 Beta 惩罚。
    /// </summary>
    public double ThompsonLatencyTargetMs { get; set; } = 800.0;
}

/// <summary>
/// 语义路由条目配置。
/// </summary>
public sealed class SemanticRouteOptions
{
    /// <summary>
    /// 路由规则名称（如 "code-generation"）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 用于相似度匹配的示例短语列表。
    /// </summary>
    public System.Collections.Generic.List<string> Phrases { get; set; } = new();

    /// <summary>
    /// 该路由规则匹配成功时指向的目标能力分档。
    /// </summary>
    public ModelTier TargetTier { get; set; } = ModelTier.Medium;
}
