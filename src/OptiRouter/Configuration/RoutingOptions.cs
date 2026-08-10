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
    /// Enables privacy-safe stable-prefix cache affinity. The policy stores only a
    /// SHA-256 fingerprint and softly promotes the previously successful model.
    /// Candidate-order changing behavior is disabled by default.
    /// </summary>
    public bool EnablePromptCacheAffinity { get; set; } = false;

    /// <summary>Stable-prefix affinity entry TTL in seconds.</summary>
    public int PromptCacheAffinityTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Enables quota-aware candidate reordering from process-local response metadata.
    /// Disabled by default; policies perform memory-only reads.
    /// </summary>
    public bool EnableQuotaAwareRouting { get; set; } = false;

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
    /// 是否启用融合路由（OpenRouter Fusion 式）。开启后，非流式请求首轮并行叫 panel 模型作答，
    /// <c>analyst</c> 模型读全部 panel 回答产出结构化分析（共识/矛盾/缺口/独特洞察），
    /// <c>outer</c> 模型再依分析写最终答案。仅非流式（流式首 chunk 锁定模型无法切换）。
    /// 成本语义：N 个 panel + 1 analyst + 1 outer ≈ N+2 次调用，是质量技术而非省钱技术，生产默认关。
    /// 与 <see cref="EnableFusionMode"/>（并行 race 取最快）正交，二者可同开；本模式在 race 之前尝试。
    /// </summary>
    public bool EnableFusionRouter { get; set; } = false;

    /// <summary>
    /// 融合路由 panel 模型数（并行作答的候选数）。默认 3，范围 [2, 5]。
    /// panel 取路由决策候选链前 N 个（策略链已过滤可用模型）。值越大答案越多样但成本/延迟越高。
    /// </summary>
    public int FusionRouterPanelSize { get; set; } = 3;

    /// <summary>Enables typed-complexity-based Fusion panel sizing. Disabled by default.</summary>
    public bool EnableDynamicFusionPanelSize { get; set; } = false;

    /// <summary>Minimum Fusion panel size when dynamic sizing is enabled.</summary>
    public int FusionRouterMinPanelSize { get; set; } = 2;

    /// <summary>Enables soft provider/family diversity for Fusion panels. Disabled by default.</summary>
    public bool EnableFusionDiversity { get; set; } = false;

    /// <summary>
    /// 融合路由 analyst 模型名。留空（默认）则用主候选（候选链首）担任 analyst。
    /// analyst 读全部 panel 回答，temp 固定 0，只产结构化 JSON 分析，不写最终答案。
    /// </summary>
    public string? FusionRouterAnalystModel { get; set; }

    /// <summary>
    /// 融合路由 analyst 的结构化分析提示词。留空时使用内置 JSON 契约提示词。
    /// 此配置独立于 <see cref="CascadeUpgradeSelfVerifyPrompt"/>，两者输出契约不同。
    /// </summary>
    public string? FusionRouterAnalystPrompt { get; set; }

    /// <summary>
    /// 融合路由 outer 模型名（写最终答案的模型）。留空（默认）则用主候选担任 outer。
    /// outer 读 analyst 分析 + 原问题写最终答案。
    /// </summary>
    public string? FusionRouterOuterModel { get; set; }

    /// <summary>
    /// 融合路由最终答案最大输出 token 数。默认 16000（对齐 OpenRouter 默认）。
    /// 仅约束 outer 写答案的 <c>MaxTokens</c>；panel 保留原请求上限，analyst 不设上限。
    /// </summary>
    public int FusionRouterMaxOutputTokens { get; set; } = 16000;

    /// <summary>
    /// 融合路由 panel / analyst 的采样温度。默认 0（确定性）。仅当原请求未显式设置 Temperature 时生效。
    /// </summary>
    public double FusionRouterTemperature { get; set; } = 0.0;

    /// <summary>
    /// 融合路由单个 panel 调用的超时秒数。0 = 不启用 panel 级超时（向后兼容，仅靠全局请求 ct 兜底）。
    /// &gt;0 时，每个 panel 绑定一个独立超时 CTS；超时的 panel 视同失败（记断路器 RecordFailure），
    /// 不阻塞 analyst——其余成功 panel 即可推进分析。全部 panel 超时/失败则回退串行。建议 30-120s。
    /// </summary>
    public int FusionRouterPanelTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// 是否启用多维能力评估路由。
    /// </summary>
    public bool EnableMultiDimensionalRouting { get; set; } = false;

    /// <summary>
    /// 是否启用 Thompson 采样自适应重排。与 <see cref="EnableLatencyAware"/> 各自独立 gate，
    /// 无需同时开启即可生效。段内按 Beta 分布采样重排，自适应探索延迟更优的模型。
    /// </summary>
    public bool EnableThompsonSampling { get; set; } = false;

    /// <summary>
    /// Thompson 采样的历史折扣/衰减因子（取值范围 [0.5, 0.99]，由 RouterOptionsValidator 强制）。
    /// 值越小，系统对端点性能变化的反应越灵敏。
    /// </summary>
    public double ThompsonDiscountFactor { get; set; } = 0.95;

    /// <summary>
    /// 理想平均延迟目标（毫秒，必须 &gt; 0，由 RouterOptionsValidator 强制）。
    /// 实际成功延迟小于该值计为 Alpha 自适应成功增量，否则（超时、大延迟或故障）计为 Beta 惩罚。
    /// </summary>
    public double ThompsonLatencyTargetMs { get; set; } = 800.0;

    /// <summary>
    /// 审计记录保留时长（小时）。超出后由后台 AuditRetentionService 周期淘汰，
    /// 防止 request_audit 无界增长。默认 168（7 天）。必须 &gt;= 1，由 RouterOptionsValidator 强制。
    /// </summary>
    public int AuditRetentionHours { get; set; } = 168;

    /// <summary>
    /// 是否启用 Prometheus 指标导出（<c>/metrics</c> 端点）。默认 true。
    /// 关闭时不注册指标中间件与 gauge 刷新服务，但 <see cref="OptiRouter.Metrics.RouterMetrics"/> 单例仍存在
    /// （ProxyOrchestrator 仍可无副作用调用 RecordAttempt，prometheus-net 的仪表为空集）。
    /// <c>/metrics</c> 端点无鉴权（同 <c>/health</c>），便于 Prometheus 抓取；仅暴露聚合数与模型名。
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Prometheus 指标导出端点路径。默认 <c>/metrics</c>。仅当 <see cref="EnableMetrics"/> 为 true 时生效。
    /// 修改后需同步更新反代/Prometheus scrape 配置。
    /// </summary>
    public string MetricsEndpointPath { get; set; } = "/metrics";
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
