using OptiRouter.Routing;

namespace OptiRouter.Configuration;

/// <summary>
/// 路由策略开关与参数。
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>
    /// 路由预设名称。可选值：<c>cost-first</c>（成本优先）、<c>balanced</c>（均衡）、<c>quality-first</c>（质量优先）。
    /// preset 只为「用户未显式配置」的项赋值，显式配置永远赢。仅作用于 Routing 节，不含 Budget。
    /// 配置文件（appsettings.json/环境变量/models-config.json）中写过的 key 视为显式配置。
    /// </summary>
    public string? Preset { get; set; } = null;

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
    /// 长输入场景下强制将候选限定到 Medium 及以下档（排除 Strong）。
    /// Strong 档的 stealth/免费层在长 prompt（>30k tokens）下延迟爆炸（p95 70s+、p99 140s+），
    /// 即使能装下也劣于 Medium 的"小但稳"模型。开启后，长 prompt 不再被路由到 Strong。
    /// 短 prompt 行为完全不受影响。默认 false（保持既有行为，便于灰度回滚）。
    /// </summary>
    public bool LongInputForceMedium { get; set; } = false;

    /// <summary>
    /// 默认能力分档。无明确信号时选哪档模型。
    /// </summary>
    public ModelTier DefaultTier { get; set; } = ModelTier.Medium;

    /// <summary>
    /// 强档延迟降级阈值（毫秒，默认 0 = 关闭）。
    /// 开启后，<see cref="LatencyAwarePolicy"/> 在段内 reorder 之前对所有 Strong 档候选做预检：
    /// 历史 p95 延迟 ≥ 本值的模型被移出原位、追加到 candidates 末尾——使段内 reorder（Thompson/Bandit/SLA）、
    /// ε 探索、failover 等下游策略在首选位置更难采样到它，从而在路由阶段就把慢强档"降级"到兜底位。
    /// 与 <see cref="LongInputForceMedium"/> 互补：后者按 prompt 长度屏蔽 Strong，前者按历史延迟屏蔽。
    /// 样本不足（&lt; <see cref="LatencyMinSamples"/>）的强档不动——避免冷启动期间误伤。
    /// </summary>
    public int LatencyDegradeStrongP95Ms { get; set; } = 0;

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
    /// 全局 Failover 请求超时时间（秒）。0 表示不限制（退回单模型 TimeoutSeconds 独立计时）。
    /// 开启后，单次请求在各候选模型间重试与降级尝试的累计耗时上限；超过此时间终止 Failover 并抛出异常。
    /// 默认 0。
    /// </summary>
    public int FailoverGlobalTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// 流式响应（SSE）的最大允许累积字节数（保护性硬限制，防止 OOM/恶意无限流输出）。
    /// 默认 20MB。
    /// </summary>
    public long MaxResponseStreamBytes { get; set; } = 20 * 1024 * 1024; // 20 MB

    /// <summary>
    /// 流式首字节（TTFT, Time To First Token）专项超时（毫秒）。0 = 禁用（退回整体 timeoutSeconds/globalTimeout 计时）。
    /// &gt;0 时，上游建立连接后若首字节在此时间内未到达，视为该候选首字节前失败：记断路器失败并 failover 到下一候选，
    /// 而非干等到整体超时。仅作用于流式（StreamAsync）；非流式已有整体超时覆盖。默认 0。
    /// </summary>
    public int StreamFirstTokenTimeoutMs { get; set; } = 0;

    /// <summary>
    /// 是否启用响应缓存（仅非流式）。开启后，按规范化请求 SHA256 精确缓存上游响应，命中即短路返回（不经路由/上游）。
    /// 适合分类/提取/翻译等幂等请求。缓存键在 PII 脱敏前计算，不会因占位符相同而串扰。默认 false。
    /// </summary>
    public bool EnableResponseCache { get; set; } = false;

    /// <summary>
    /// 响应缓存单条 TTL（秒）。默认 3600。启用 <see cref="EnableResponseCache"/> 时必须 &gt;0。
    /// </summary>
    public int ResponseCacheTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// 响应缓存最大条目数（软上限，防 OOM）。默认 1000。超上限后新条目不再写入。
    /// </summary>
    public int ResponseCacheMaxEntries { get; set; } = 1000;

    /// <summary>
    /// 响应缓存字节预算（软上限）。缓存的是完整响应体，MB 级大响应 × 条目数上限会无界吃内存——
    /// 按条目 UTF-8 体量估算累计，超预算后新条目不再写入，淘汰时归还。默认 128MB，0 = 不限字节。
    /// </summary>
    public long ResponseCacheMaxBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// 成本感知权重 α ∈ [0,1]。0=禁用（默认，reward 仅看延迟/质量）。
    /// &gt;0 时 reward = (1-α)·原reward + α·costReward，引导学习状态在质量/延迟相近时偏好便宜模型。建议 0.2~0.4。
    /// </summary>
    public double CostAwareWeight { get; set; } = 0.0;

    /// <summary>
    /// 成本归一化基准（美元/每百万 token，即等效 $/M 混合价格）。costReward = baseline/(baseline+pricePerMillion)，
    /// pricePerMillion 由本次请求花费按 token 数归一化（cost×1e6/tokens），消除"长输入=贵模型"的系统性偏差——
    /// 绝对花费随输入长度线性增长，不归一化会把长上下文请求的所有模型都误判为贵。
    /// 模型价格等于 baseline 时 costReward=0.5。默认 1.0（对应中档模型价位：Cheap≈$0.15/M，Strong≈$5-15/M）。
    /// token 数未知（=0）时回退用绝对花费（USD）对比基准，此时建议把基准配成单请求典型花费。
    /// 启用 <see cref="CostAwareWeight"/> 时必须 &gt;0。
    /// </summary>
    public decimal CostAwareBaselineUsd { get; set; } = 1.0m;

    /// <summary>
    /// 质量惩罚因子 ∈ [0.0, 1.0]，由 RouterOptionsValidator 强制。主路径成功请求若检测到低质量信号
    /// （<c>finish_reason=length</c> 截断 / <c>content_filter</c> / 空 content），reward 按此因子乘性折减：
    /// <c>finalReward = latencyReward × qualityFactor</c>。默认 0.3（低质量打三折，保留正信号但不鼓励）；1.0=不惩罚。
    /// 仅作用于延迟映射路径（<c>RecordThompsonOutcome</c>），不影响显式质量入口与竞速取消。
    /// </summary>
    public double QualityPenaltyFactor { get; set; } = 0.3;

    /// <summary>
    /// 是否启用 regenerate 负反馈。开启后，同一规范化请求（SHA256 键，与响应缓存同源）在窗口时间内
    /// 再次到达且上次为成功响应时，视为用户对上次答案不满意（regenerate），给上次命中的模型
    /// 注入 <see cref="RegeneratePenaltyReward"/> 低 reward。零额外调用成本的质量信号。
    /// 注意：对同一请求的例行重复调用（如定时任务固定 prompt）也会被误判为 regenerate，
    /// 存在此类流量的部署应保持关闭。默认 false。
    /// </summary>
    public bool EnableRegenerateFeedback { get; set; } = false;

    /// <summary>
    /// regenerate 负反馈注入的 reward ∈ [0.0, 1.0]，由 RouterOptionsValidator 强制。
    /// 默认 0.1（低于慢成功地板 0.3，强负反馈但不等同硬失败 0.0——regenerate 也可能只是想要不同表述）。
    /// </summary>
    public double RegeneratePenaltyReward { get; set; } = 0.1;

    /// <summary>
    /// regenerate 判定窗口（秒）。上次成功响应距今超过窗口的同键请求不再视为 regenerate
    /// （隔天重发同一问题大概率是独立请求）。默认 600（10 分钟）。必须 &gt; 0。
    /// </summary>
    public int RegenerateFeedbackWindowSeconds { get; set; } = 600;

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
    /// 单次探活的基准超时秒数。默认 10。有延迟统计的模型按平均 TTFT 自适应放宽
    /// （TTFT×1.5 + 2s，上限 60s），避免慢首 token 模型（如 TTFT 40s+）被探活误判超时熔断。
    /// 最小 5（低于 5 按 5 计）。
    /// </summary>
    public int HealthProbeTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// 近期成功流量的新鲜窗口秒数。真实请求或探活成功距今不足该窗口的模型跳过主动探活：
    /// 活跃模型由真实流量背书健康，探活只会重复计费并引入误判（探活 401/超时熔断健康模型）。
    /// 默认 300。0 = 不跳过（始终探活）。
    /// </summary>
    public int HealthProbeFreshSuccessSkipSeconds { get; set; } = 300;

    /// <summary>
    /// 是否启用向量空间语义路由器。
    /// </summary>
    public bool EnableSemanticRouter { get; set; } = true;

    /// <summary>
    /// 匹配模式："Hybrid"（TF-IDF 高置信短路 + 第二阶段）| "TfIdf" | "Dense"。
    /// 内置 Dense 是稳定词法特征哈希，不是训练 embedding；仅注入自定义引擎时才具备对应语义能力。
    /// </summary>
    public string SemanticRouterMode { get; set; } = "Hybrid";

    /// <summary>
    /// Hybrid 模式下 TF-IDF 高置信短路阈值。达到阈值直接返回，否则交给第二阶段判定。默认 0.45。
    /// </summary>
    public double HybridHighConfidenceThreshold { get; set; } = 0.45;

    /// <summary>
    /// 向量余弦相似度匹配阈值。取值范围 [0.0, 1.0]。默认 0.25。
    /// </summary>
    public double SemanticSimilarityThreshold { get; set; } = 0.25;

    /// <summary>
    /// 语义路由规则列表。
    /// </summary>
    public System.Collections.Generic.List<SemanticRouteOptions> SemanticRoutes { get; set; } = new();

    /// <summary>
    /// 是否启用 ONNX 本地轻量级向量模型进行深层隐式语义路由。
    /// 开启后自动加载本地 ONNX 模型 (如 bge-small-zh / all-MiniLM-L6-v2) 替换默认词法特征哈希。
    /// </summary>
    public bool EnableOnnxEmbedding { get; set; } = false;

    /// <summary>
    /// 本地 ONNX Embedding 模型文件路径。
    /// </summary>
    public string? OnnxModelPath { get; set; } = null;

    /// <summary>
    /// ONNX 执行提供者，可选 "CPU" 或 "CUDA"。默认 "CPU"。
    /// </summary>
    public string OnnxExecutionProvider { get; set; } = "CPU";

    /// <summary>
    /// 是否启用原生 OpenTelemetry OTLP Exporter 链路追踪导出。
    /// 开启后可将 OptiRouter 的 ActivitySource DAG 分布式追踪直接导出至 Jaeger, Tempo 或 Datadog。
    /// </summary>
    public bool EnableOtlpTracing { get; set; } = false;

    /// <summary>
    /// OTLP 接收端点 URL（如 "http://localhost:4317" 或 "http://jaeger:4318/v1/traces"）。
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// OTLP 传输协议：可选 "grpc" 或 "http/protobuf"。默认 "grpc"。
    /// </summary>
    public string OtlpProtocol { get; set; } = "grpc";

    /// <summary>
    /// OpenTelemetry 导出的服务名称。默认 "OptiRouter"。
    /// </summary>
    public string OtlpServiceName { get; set; } = "OptiRouter";

    /// <summary>
    /// 是否启用深度语义向量响应缓存 (Semantic Response Cache)。
    /// 开启后将基于语义相似度算法匹配历史高相似度 Prompt 缓存，实现 0 上游成本与亚毫秒极速响应。
    /// </summary>
    public bool EnableSemanticCache { get; set; } = false;

    /// <summary>
    /// 语义响应缓存命中的最低 Cosine 余弦相似度阈值。范围 [0.80, 0.99]，默认 0.95。
    /// </summary>
    public float SemanticCacheSimilarityThreshold { get; set; } = 0.95f;

    /// <summary>
    /// 语义响应缓存项生存时间（分钟）。默认 60 分钟。
    /// </summary>
    public int SemanticCacheTtlMinutes { get; set; } = 60;

    /// <summary>
    /// 语义响应缓存最大条目数。超出时触发 LRU/过期清理。默认 10000。
    /// </summary>
    public int SemanticCacheMaxEntries { get; set; } = 10000;

    /// <summary>
    /// 是否启用基于 TCP Vegas / AIMD 算法的上游自适应并发拥塞控制。
    /// 开启后可针对上游 API 延迟飙升（拥塞）动态收缩并发许可，防爆线程池与内存。
    /// </summary>
    public bool EnableAdaptiveConcurrency { get; set; } = false;

    /// <summary>
    /// 自适应并发限制单模型最小允许并发许可数。默认 2。
    /// </summary>
    public int AdaptiveMinLimit { get; set; } = 2;

    /// <summary>
    /// 自适应并发限制单模型最大允许并发许可数。默认 50。
    /// </summary>
    public int AdaptiveMaxLimit { get; set; } = 50;

    /// <summary>
    /// 默认 SLA 路由模式（Balanced 综合延迟 / Ttft 首 Token 敏捷度 / Tps 生成吞吐率）。
    /// 可通过 HTTP Header 'X-OptiRouter-SLA'（ttft / tps / balanced）覆盖单请求级别。
    /// </summary>
    public SlaMode DefaultSlaMode { get; set; } = SlaMode.Balanced;

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
    /// 会话粘性"延迟熔断"逃生阈值（毫秒，默认 0 = 关闭）。
    /// 开启后，若该 session 最近 <see cref="SessionAffinityEscapeWindowSize"/> 次请求的平均延迟
    /// 超过本值，则本轮跳过粘性、走主链重新决策。
    /// 解决"session 已被粘到慢模型上"问题（如被粘到 stealth/ox-alpha 的 session 长时间 30-100s）。
    /// 失败请求的延迟不计入窗口（不污染分布）。默认 0 保持既有行为。
    /// </summary>
    public int SessionAffinityEscapeAvgLatencyMs { get; set; } = 0;

    /// <summary>
    /// 会话粘性"延迟熔断"窗口大小（最近 N 次成功请求的延迟平均）。默认 5。
    /// 必须 >= 1。<see cref="SessionAffinityEscapeAvgLatencyMs"/> = 0 时本字段无意义。
    /// </summary>
    public int SessionAffinityEscapeWindowSize { get; set; } = 5;

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
    /// 级联自校验判定 CONFIDENT 时，给 Cheap 模型注入的 Thompson/Bandit 质量 reward [0.0, 1.0]。默认 1.0。
    /// 此前自校验的置信度判定被丢弃，学习状态只看延迟+硬失败，系统性偏好"快但不一定准"的模型。
    /// 置信 = 答案质量高 → 正反馈强化该 Cheap 模型。
    /// </summary>
    public double CascadeUpgradeConfidentReward { get; set; } = 1.0;

    /// <summary>
    /// 级联自校验判定 UNCERTAIN（并触发升级）时，给 Cheap 模型注入的 Thompson/Bandit 质量 reward [0.0, 1.0]。默认 0.0。
    /// 不置信 = 答案质量不足 → 负反馈惩罚该 Cheap 模型，使后续路由降低对其偏好。
    /// </summary>
    public double CascadeUpgradeUncertainReward { get; set; } = 0.0;

    /// <summary>
    /// 级联自校验用的校验模型名（他评，消除"模型自评"的自利偏差）。留空（默认）= 回退自评（用 Cheap 模型校验自己的答案）。
    /// 配置时从已启用模型中按名匹配；找不到则告警并回退自评。建议配置一个更强的模型（如 Strong）做校验，可信度高于自评。
    /// 校验调用的成本按实际校验模型价格入账。
    /// </summary>
    public string? CascadeUpgradeVerifierModel { get; set; } = null;

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
    /// Hedged request 延迟触发阈值（毫秒）。0 = 总是并行（当前默认行为，每次 N× 成本）。
    /// &gt;0 时改为 hedging：admitted[0]（路由主）立即启动；admitted[1..]（hedged）先等待此延迟，
    /// 延迟内主请求成功（raceCts 取消）则 hedged 不启动（1× 成本），否则延迟到期启动 hedged 并行。
    /// 显著降低正常情况下的成本（仅尾延迟场景才并行）。仅非流式。默认 0。
    /// </summary>
    public int FusionHedgeDelayMs { get; set; } = 0;

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
    /// 融合路由 panel 专用采样温度。null（默认）= 沿用 <see cref="FusionRouterTemperature"/>，向后兼容。
    /// panel 用于发散采样，建议配置 &gt;0 以引入多样性（对齐 Self-Consistency 的温度多样性收益）；
    /// analyst/outer 仍用 <see cref="FusionRouterTemperature"/>（低温度保结构化 JSON 稳定）。
    /// 仅当原请求未显式设置 Temperature 时生效。
    /// </summary>
    public double? FusionRouterPanelTemperature { get; set; }

    /// <summary>
    /// 融合路由最低复杂度门控。默认 <see cref="OptiRouter.Routing.RequestComplexity.Unknown"/>（0，无门控）：
    /// 所有复杂度请求都触发融合，等同旧行为（向后兼容——RuleClassifier 关闭时复杂度为 Unknown，
    /// 不应被跳过）。设为 <see cref="OptiRouter.Routing.RequestComplexity.Standard"/> 可让 Simple（及 Unknown）
    /// 请求跳过融合，省去 ×N panel 成本。设为 <see cref="OptiRouter.Routing.RequestComplexity.Unknown"/> 即关闭门控。
    /// 与 <see cref="EnableDynamicFusionPanelSize"/> 正交：前者 gate 是否融合，后者定 panel 数。
    /// </summary>
    public OptiRouter.Routing.RequestComplexity FusionRouterMinComplexity { get; set; } = OptiRouter.Routing.RequestComplexity.Unknown;

    /// <summary>
    /// 融合路由单个 panel 调用的超时秒数。0 = 不启用 panel 级超时（向后兼容，仅靠全局请求 ct 兜底）。
    /// &gt;0 时，每个 panel 绑定一个独立超时 CTS；超时的 panel 视同失败（记断路器 RecordFailure），
    /// 不阻塞 analyst——其余成功 panel 即可推进分析。全部 panel 超时/失败则回退串行。建议 30-120s。
    /// </summary>
    public int FusionRouterPanelTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// 是否启用 PII 敏感数据脱敏与反向还原。默认 false。
    /// </summary>
    public bool EnablePiiAnonymization { get; set; } = false;

    /// <summary>
    /// 是否启用数据不出域/本地私有节点隔离路由策略。默认 false。
    /// </summary>
    public bool EnableDataSovereignty { get; set; } = false;

    /// <summary>
    /// 是否启用 JSON AST 自动化修补服务（修复 Markdown 围栏、控制字符、断尾补全）。默认 true。
    /// </summary>
    public bool EnableJsonAstAutoRepair { get; set; } = true;

    /// <summary>
    /// 是否启用 W3C 分布式链路追踪（生成 TraceId / SpanId，映射 ActivitySource）。默认 true。
    /// </summary>
    public bool EnableDistributedTracing { get; set; } = true;

    /// <summary>
    /// 是否启用多轮对话人设一致性防护（Persona Drift Protection）。默认 false。
    /// 会话兜底派生（DeriveConversationSession）上线后所有请求都有会话 ID，
    /// 该开关若默认 true 会向 system 消息注入人设提示、改变请求原文（破坏上游 prompt cache
    /// 前缀与既有请求语义）——改为显式开启。
    /// </summary>
    public bool EnablePersonaDriftProtection { get; set; } = false;

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
    /// 理想平均延迟目标（毫秒，必须 &gt; 0，由 RouterOptionsValidator 强制）。作为 <see cref="ThompsonLatencyTargetMsByTier"/>
    /// 未覆盖 tier 的回退目标。实际延迟经 <c>OutcomeRecorder.MapLatencyToReward</c> 平滑映射为 reward（越快越高，非阶跃）。
    /// </summary>
    public double ThompsonLatencyTargetMs { get; set; } = 800.0;

    /// <summary>
    /// Per-tier 延迟目标（毫秒）。键为 <see cref="ModelTier"/>，值为该 tier 的延迟目标。
    /// 命中且 &gt;0 时覆盖 <see cref="ThompsonLatencyTargetMs"/>；未配置的 tier 回退全局目标。
    /// 消除"全局单 target 系统性偏 Cheap"——强模型天生慢，用更宽松的目标避免被系统性惩罚。
    /// 默认 {Strong:1500, Medium:1000, Cheap:600}。仅 <see cref="EnableThompsonSampling"/> 启用时各值必须 &gt;0。
    /// </summary>
    public System.Collections.Generic.Dictionary<ModelTier, double> ThompsonLatencyTargetMsByTier { get; set; } = new()
    {
        { ModelTier.Strong, 15000.0 },
        { ModelTier.Medium, 5000.0 },
        { ModelTier.Cheap, 2000.0 }
    };

    /// <summary>
    /// 竞速失败（并行 racing 中被更快模型比下去而取消）的 Thompson 部分奖励。
    /// 取值范围 [0.0, 1.0]（由 RouterOptionsValidator 强制）；0.0=等效硬失败，1.0=等效快成功。
    /// 默认 0.5：高于慢成功 0.3、低于快成功 1.0。可独立调参，按观测效果（如模型取消率 vs 采纳后成功率）调整。
    /// </summary>
    public double ThompsonRaceCancelledReward { get; set; } = 0.5;

    /// <summary>
    /// 延迟归一化基准输出 token 数。0 = 禁用（默认，完全保持现行为）。
    /// &gt;0 时，completionTokens 超过基准的请求，其延迟按 <c>elapsedMs × refTokens / completionTokens</c> 折算后再映射 reward——
    /// 输出比基准长多少倍，延迟就宽恕多少倍，消除"长答案=慢模型"的系统性惩罚。
    /// 例如 refTokens=500、completionTokens=2000、elapsed=2000ms → 有效延迟 500ms（折算后），奖励显著高于未归一化时的值。
    /// 归一化仅应用于输出长度超出基准的请求（短于基准的请求延迟不变，避免反向惩罚简洁模型）。
    /// </summary>
    public int ThompsonLatencyNormalizeRefTokens { get; set; } = 0;

    /// <summary>
    /// 是否启用上下文老虎机（Contextual Bandit / LinUCB）路由。默认 false。
    /// 用分类信号（one-hot）+ tier 构造上下文特征向量，每模型维护线性 θ + 协方差，
    /// 段内按 LinUCB 打分（θ·x + α·sqrt(xᵀA⁻¹x)）重排，替代非上下文 Thompson。
    /// 修非上下文 Thompson 「只优化延迟、系统性低估 Strong」的缺陷（研究实证 gpt-4o regret 0.447）。
    /// 与 <see cref="EnableThompsonSampling"/> 互斥——同一段内只能由一种重排策略负责，混用会让
    /// 状态互相覆盖、stat 计数器错位。<c>RouterOptionsValidator</c> 启动期强制拒绝两者同时开启。
    /// </summary>
    public bool EnableContextualBandit { get; set; } = false;

    /// <summary>
    /// LinUCB 探索系数 α（Upper Confidence Bound 权重）。默认 1.0。
    /// 越大越倾向探索（选择样本不足/高不确定模型）；越小越倾向利用（选当前最优估计）。
    /// </summary>
    public double ContextualBanditAlpha { get; set; } = 1.0;

    /// <summary>
    /// 上下文老虎机历史折扣/衰减因子（取值范围 [0.5, 0.99]，由 RouterOptionsValidator 强制）。
    /// 值越小，系统对端点性能变化的反应越灵敏。
    /// </summary>
    public double ContextualBanditDiscountFactor { get; set; } = 0.95;

    /// <summary>
    /// ε 探索保底 ∈ [0.0, 1.0]，由 RouterOptionsValidator 强制。默认 0（关闭）。
    /// &gt;0 时，延迟感知/Thompson/Bandit 段内重排后以概率 ε 把一个随机非首位模型提到段首，
    /// 保证尾部模型持续获得真实流量样本。修低流量下的"尾部锁死"反馈环：
    /// 重排决定尝试顺序 → 链尾模型只在异常场景被尝试 → 样本全部来自异常 → 采样分长期偏低 → 永远翻不了身。
    /// 自用低流量建议 0.05（5% 请求偶尔慢一次可接受）；0.0 保持纯贪心。
    /// 仅在同 tier 段 ≥2 候选且段内重排（latency/thompson/bandit 任一启用）时生效。
    /// </summary>
    public double ExplorationEpsilon { get; set; } = 0.0;

    /// <summary>
    /// 探索饥饿阈值（样本数）。默认 0（关闭定向探索，保持旧行为：均匀随机提升）。
    /// &gt;0 时，ε 探索优先提升样本饥饿的模型（进程内 ThompsonStateStore.ModelStats.N 低于此值），
    /// 把探索预算定向给最缺样本的模型。段内所有模型样本充足时回退均匀随机（保留探索保底语义）。
    /// 样本计数为进程内统计（重启归零），反映该模型在本进程内的真实请求积累。
    /// </summary>
    public long ExplorationStarvedN { get; set; } = 0;

    /// <summary>
    /// 审计记录保留时长（小时）。超出后由后台 AuditRetentionService 周期淘汰，
    /// 防止 request_audit 无界增长。默认 0（永久保留，不淘汰）；正数按窗口淘汰。
    /// 负数无语义，由 RouterOptionsValidator 强制（必须 &gt;= 0）。
    /// </summary>
    public int AuditRetentionHours { get; set; } = 0;

    /// <summary>
    /// 是否在审计记录中存储请求内容明文（<c>request_content</c> 字段，用于 Dashboard 展示）。
    /// 默认 false。管理员可显式开启以在 Dashboard 展示请求内容；关闭后审计与 Dashboard 不再留存请求内容明文。
    /// </summary>
    public bool AuditStoreRequestContent { get; set; } = false;

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

    /// <summary>
    /// Prometheus 指标端点访问密钥（可选）。配置后，访问 <c>/metrics</c> 需提供 Bearer token；
    /// 未配置或为 null 时端点无鉴权（默认，向后兼容）。格式：请求头 <c>Authorization: Bearer &lt;MetricsApiKey&gt;</c>。
    /// </summary>
    public string? MetricsApiKey { get; set; } = null;

    /// <summary>
    /// 告警 Webhook 推送 URL。留空时禁用告警推送（告警仅在 Dashboard 展示）。
    /// 支持任意 HTTP 端点（Slack/钉钉/飞书/自建）。告警出现推送 <c>alert</c> 事件，
    /// 恢复后推送 <c>resolved</c> 事件；推送失败自动重试。
    /// </summary>
    public string AlertWebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// 告警检查与推送周期（秒）。默认 30。
    /// </summary>
    public int AlertWebhookIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 是否启用零拷贝 SSE 流式滑动窗口敏感词与合规在线拦截。
    /// </summary>
    public bool EnableStreamingComplianceFilter { get; set; } = false;

    /// <summary>
    /// 流式在线检测敏感词列表。
    /// </summary>
    public System.Collections.Generic.List<string> StreamingSensitiveKeywords { get; set; } = new();

    /// <summary>
    /// 流式合规违规动作（Block 立即中断流 / Redact 掩码替代）。默认 Block。
    /// </summary>
    public OptiRouter.Compliance.ComplianceAction StreamingComplianceAction { get; set; } = OptiRouter.Compliance.ComplianceAction.Block;

    /// <summary>
    /// 流式敏感词掩码替换文本。仅当 StreamingComplianceAction 为 Redact 时生效。默认 "***"。
    /// </summary>
    public string StreamingComplianceReplacementMask { get; set; } = "***";

    /// <summary>
    /// 是否启用内容审核（Moderation）。默认关闭。启用时需配置 <see cref="ModerationEndpoint"/>。
    /// </summary>
    public bool EnableContentModeration { get; set; } = false;

    /// <summary>
    /// 内容审核端点（OpenAI Moderation API 兼容，如 <c>https://api.openai.com/v1/moderations</c>）。
    /// 留空时不注册审核器（功能禁用）。
    /// </summary>
    public string ModerationEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// 内容审核 API Key。留空则请求不带鉴权头。
    /// </summary>
    public string ModerationApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 违规判定阈值：category_scores 中最高分数达到该值即判违规。默认 0.8。
    /// </summary>
    public double ModerationThreshold { get; set; } = 0.8;

    /// <summary>
    /// 输入审核违规动作（用户消息）。默认 Block（拒绝请求）。
    /// </summary>
    public OptiRouter.Compliance.ModerationAction ModerationInputAction { get; set; } = OptiRouter.Compliance.ModerationAction.Block;

    /// <summary>
    /// 输出审核违规动作（模型响应）。默认 Block（中断响应）。Redact 为后续扩展。
    /// </summary>
    public OptiRouter.Compliance.ModerationAction ModerationOutputAction { get; set; } = OptiRouter.Compliance.ModerationAction.Block;

    /// <summary>
    /// 审核采样率（0~1），用于控制审核 API 成本。默认 1.0（全量审核）。
    /// </summary>
    public double ModerationSampleRate { get; set; } = 1.0;

    /// <summary>
    /// 是否启用卡尔曼滤波与 P99 动态降权负载均衡。
    /// 开启后结合 1D 卡尔曼滤波平滑估计真实隐藏延迟，并对高尾延 P99 异常 Provider 进行指数级降权。
    /// </summary>
    public bool EnableKalmanLoadBalance { get; set; } = false;

    /// <summary>
    /// 卡尔曼滤波 P99 降权的目标 SLA 延迟（毫秒）。默认 1000ms。
    /// </summary>
    public double KalmanTargetLatencyMs { get; set; } = 1000.0;

    /// <summary>
    /// 卡尔曼滤波 P99 超限降权的惩罚指数因子 γ。默认 1.5。
    /// </summary>
    public double KalmanPenaltyGamma { get; set; } = 1.5;

    /// <summary>
    /// 是否启用 Cost-Quality 帕累托前沿动态调节器。
    /// </summary>
    public bool EnableParetoFrontierRegulator { get; set; } = false;

    /// <summary>
    /// 帕累托前沿 Utility 质量权重因子 λ \in [0, 1]。默认 0.7 (70% 质量, 30% 成本)。
    /// </summary>
    public double ParetoQualityWeight { get; set; } = 0.7;

    /// <summary>
    /// 是否启用严格帕累托前沿过滤（过滤掉被其他模型绝对支配的劣势模型）。
    /// </summary>
    public bool ParetoStrictFrontierFilter { get; set; } = false;

    /// <summary>
    /// 是否启用 KV-Cache 空间局部性与 Radix Trie 前缀亲和性路由。
    /// </summary>
    public bool EnableKvCacheLocality { get; set; } = false;

    /// <summary>
    /// KV-Cache 上游匹配有效生命周期（分钟）。默认 10 分钟。
    /// </summary>
    public int KvCacheTtlMinutes { get; set; } = 10;

    /// <summary>
    /// 是否启用 Reasoning Token 动态计算预算调节器。
    /// </summary>
    public bool EnableReasoningBudgetController { get; set; } = false;

    /// <summary>
    /// 简单任务 Reasoning 计算预算最大 Token 数。默认 1024。
    /// </summary>
    public int ReasoningLowMaxTokens { get; set; } = 1024;

    /// <summary>
    /// 标准任务 Reasoning 计算预算最大 Token 数。默认 4096。
    /// </summary>
    public int ReasoningMediumMaxTokens { get; set; } = 4096;

    /// <summary>
    /// 高难度任务 Reasoning 计算预算最大 Token 数。默认 16384。
    /// </summary>
    public int ReasoningHighMaxTokens { get; set; } = 16384;

    /// <summary>
    /// 是否启用拜占庭容错 (BFT) 与多模型多重共识一致性校验。
    /// </summary>
    public bool EnableByzantineConsensus { get; set; } = false;

    /// <summary>
    /// 拜占庭共识判定为异常幻觉/偏离的相似度门限。默认 0.65。
    /// </summary>
    public double ByzantineOutlierThreshold { get; set; } = 0.65;

    /// <summary>
    /// 是否启用时序预测主动弹性避浪路由。
    /// </summary>
    public bool EnablePredictiveResilience { get; set; } = false;

    /// <summary>
    /// 时序预测主动超前预测窗口（分钟）。默认 2 分钟。
    /// </summary>
    public int PredictiveLookaheadMinutes { get; set; } = 2;

    /// <summary>
    /// 是否启用知识库与动态 RAG 检索感知路由。
    /// </summary>
    public bool EnableRagAwareRouting { get; set; } = false;

    /// <summary>
    /// RAG 上下文高充分度阈值（&gt;= 此阈值时说明检索知识极为充分，优先调度 Cheap/Medium 经济模型）。默认 0.70。
    /// </summary>
    public double RagHighSufficiencyThreshold { get; set; } = 0.70;

    /// <summary>
    /// RAG 上下文低充分度阈值（&lt;= 此阈值时说明检索知识匮乏或冲突，强制提升调度至 Strong 深度推理模型）。默认 0.35。
    /// </summary>
    public double RagLowSufficiencyThreshold { get; set; } = 0.35;

    /// <summary>
    /// 是否启用分布式跨网关集群状态同步网格 (Distributed State Mesh)。
    /// </summary>
    public bool EnableDistributedStateMesh { get; set; } = false;

    /// <summary>
    /// 分布式网格节点唯一标识。留空则自动生成随机 Node ID。
    /// </summary>
    public string MeshNodeId { get; set; } = string.Empty;

    /// <summary>
    /// 分布式状态网格的 Redis 连接串。留空时使用进程内 InMemory 网格（单实例部署）；
    /// 多网关实例共享同一 Redis 时启用集群级状态同步。连接失败自动降级 InMemory。
    /// </summary>
    public string MeshRedisConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 是否向集群网格广播 KV Cache 前缀索引。默认 true。
    /// </summary>
    public bool MeshBroadcastKvCache { get; set; } = true;

    /// <summary>
    /// 是否向集群网格广播卡尔曼延迟观测数据。默认 true。
    /// </summary>
    public bool MeshBroadcastKalman { get; set; } = true;

    /// <summary>
    /// 是否向集群网格广播成本账本消耗。默认 true。
    /// </summary>
    public bool MeshBroadcastCostLedger { get; set; } = true;

    /// <summary>
    /// 是否向集群网格广播主动弹性故障时序事件。默认 true。
    /// </summary>
    public bool MeshBroadcastResilience { get; set; } = true;

    /// <summary>
    /// 是否启用 MCP (Model Context Protocol) 统一生态集成。
    /// </summary>
    public bool EnableMcpIntegration { get; set; } = true;

    /// <summary>
    /// 是否启用 MCP 工具调用执行闭环（模型请求工具时执行并重放，直至无 tool_calls 或达轮次上限）。
    /// 默认关闭；仅非流式请求生效。
    /// </summary>
    public bool EnableMcpToolExecution { get; set; } = false;

    /// <summary>
    /// MCP 工具执行的最大重放轮数（每轮执行全部 tool_calls 后向模型重放一次）。默认 4。
    /// </summary>
    public int MaxMcpToolRounds { get; set; } = 4;

    /// <summary>
    /// 是否启用 MCP / Tool Schema 复杂度感知动态分级路由。
    /// </summary>
    public bool EnableMcpComplexityRouting { get; set; } = true;

    /// <summary>
    /// 是否启用 Tool Call JSON 参数自愈与清洗器（自动修复尾随逗号、单引号、Markdown 包裹与括号截断）。
    /// </summary>
    public bool EnableMcpToolCallSanitizer { get; set; } = true;

    /// <summary>
    /// 是否启用自适应提示词压缩与 Token 动态瘦身引擎。默认 true。
    /// </summary>
    public bool EnablePromptCompression { get; set; } = true;

    /// <summary>
    /// 提示词压缩与 Token 动态瘦身配置。
    /// </summary>
    public OptiRouter.Compression.PromptCompressionOptions PromptCompression { get; set; } = new();
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
