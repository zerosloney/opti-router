# 配置参考（全量字段）

> 运行时权威配置在**配置库**（Dashboard 编辑 → 落库 → 热生效，无需重启）；`appsettings*.json` 仅承担部署设置与首次播种。
> 三层阅读路径：[README 核心配置](../README.md#核心配置90-场景只需这一节)（90% 场景）→ 本页全量参考 → [RoutingOptions.cs](../src/OptiRouter/Configuration/RoutingOptions.cs) 注释（口径最细，字段语义以代码注释为准）。

## 全局

| 字段 | 含义 | 默认 |
|------|------|------|
| `ConfigDbConnectionString` | 配置库连接串（MariaDB）。路由/预算/模型/租户 Key/审计/学习状态的唯一权威存储；未配置时回退 SQLite `data/optirouter-config.db` | *空* |
| `AdminApiKey` | 管理密钥**首启种子**：首次启动时以 SHA256 哈希入库后即被忽略；两者皆缺则生成随机密钥并打印启动日志一次。轮换 = 清空配置库 `security` scope 后重启 | *空* |
| `AdminCookieRequireHttps` | 管理台登录 Cookie 是否强制 `Secure` 标记（反代 TLS 终结时按需配置） | `false` |
| `urls` | 监听地址（生产建议在 appsettings.Production.json 钉死，如 `http://localhost:5080`） | `http://localhost:5000` |

## 入站安全

| 字段 | 含义 | 默认 |
|------|------|------|
| `RequestsPerMinute` | 每个分区（IP > Auth）的固定窗口每分钟请求上限 | `60` |
| `MaxConcurrentRequestsPerPartition` | 每个分区同时进行的最大请求数，超出返回 429 | `100` |

> **分区 Key 优先级**：客户端 IP（启用 `TrustProxyHeaders` 时依次使用 `CF-Connecting-IP`、`X-Forwarded-For` 首段，否则使用 `RemoteIpAddress`）> Bearer Token（SHA256 哈希前 16 hex）。仅当无法取得 IP 时按认证标识分区；`X-Session-Id` 不参与限流分区。

## Models[]（模型端点列表）

| 字段 | 含义 | 示例 |
|------|------|------|
| `Name` | 模型标识 | `gpt-4o` |
| `BaseUrl` | 上游 API 基地址 | `https://api.openai.com/v1` |
| `ApiKey` | 鉴权密钥。支持 `env:VAR_NAME` 语法从环境变量加载（变量缺失时该模型 key 为空并告警）。模型配置权威存储为配置库（SQLite 或 MariaDB，见部署配置）；通过 Dashboard 保存模型配置会写入配置库并热生效 | `sk-...` |
| `Tier` | 能力分档：`Strong` / `Medium` / `Cheap` | `Strong` |
| `MaxContextTokens` | 最大上下文长度 | `128000` |
| `InputPricePerMillion` | 输入价格（美元/百万 token） | `2.5` |
| `CachedInputPricePerMillion` | 缓存命中输入价格（美元/百万 token）；省略/null 时回退普通输入价格 | `1.25` |
| `CacheWriteInputPricePerMillion` | 缓存写入输入价格（美元/百万 token）；省略/null 时回退普通输入价格 | `3.0` |
| `OutputPricePerMillion` | 输出价格（美元/百万 token） | `10.0` |
| `Provider` | 可选 provider 标识（自由字符串），仅用于 Fusion 软多样性；空表示未知 | `openai` |
| `Family` | 可选模型家族标识（自由字符串），仅用于 Fusion 软多样性；空表示未知 | `gpt-4o` |
| `TimeoutSeconds` | 单次调用超时秒数。非流式=总时长上限；流式=响应头阶段总时长上限 + 相邻 chunk 空闲上限（持续推进的流不设总时长上限，不会被中途切断） | `120` |
| `MaxRetries` | 失败后最大重试次数 | `0` |
| `Enabled` | 是否启用该模型 | `true` |
| `IsLocalOrPrivate` | 标识该端点是否为本地/私有化节点（用于数据不出域隔离） | `false` |
| `Tags` | 能力标签，配合 `EnableCapabilityFilter` 使用。约定值：`vision`（图片输入）、`tool-use`（函数调用）、`json-mode`（`response_format: json_object`） | `["vision", "tool-use"]` |

## Budget（预算控制）

| 字段 | 含义 | 示例 |
|------|------|------|
| `DailyBudgetUsd` | 日预算（美元） | `10.0` |
| `SessionBudgetUsd` | 会话预算（美元），null 表示不限 | `null` |
| `EnforceOnExhausted` | 耗尽行为：`Degrade` 降级 / `Reject` 拒绝 | `Degrade` |
| `StoreProvider` | 持久化存储提供者，默认 `Auto`：配置了全局 `OptiRouter:ConfigDbConnectionString` 即用 MariaDb，否则回退 SQLite——只配连接串一处即全量切换；显式指定 `Sqlite` / `MariaDb` / `Postgres` / `Redis` / `InMemory` 可覆盖，服务器型 DB 供多实例共享全局账本 | `Auto` |
| `MariaDbConnectionString` | 可选覆盖，缺省回退全局 `OptiRouter:ConfigDbConnectionString`（同一数据库只配一处连接）；两者皆空且 `StoreProvider=MariaDb` 时启动校验失败 | *回退全局* |
| `UsePersistentStore` | 是否持久化成本账本（跨重启保留）；服务器型提供者（MariaDb/Postgres/Redis）忽略此开关 | `true` |
| `StorePath` | SQLite 账本文件路径，仅 `StoreProvider=Sqlite` 且 `UsePersistentStore=true` 时生效 | `data/optirouter-budget.db` |
| `SessionEvictionHours` | 会话账户淘汰年龄（小时）；超过此时间无活动的会话自动清理，防止内存泄漏 | `24` |

## Routing（路由策略）

字段语义按主题分组；组合语义（哪些开关一起开才有意义）见管理台保存配置时返回的组合诊断，或 [RoutingConfigDiagnostics](../src/OptiRouter/Routing/RoutingConfigDiagnostics.cs)。

### 学习与质量信号

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableThompsonSampling` | Thompson 采样自适应重排。与 `EnableLatencyAware` 各自独立 gate，无需同时开启；段内按 Beta 分布采样重排，自适应探索延迟更优的模型 | `false` |
| `ThompsonDiscountFactor` | Thompson 历史折扣/衰减因子 `[0.5, 0.99]`，越小对端点性能变化越灵敏 | `0.95` |
| `ThompsonLatencyTargetMs` | 理想平均延迟目标（毫秒），作为 per-tier 目标未覆盖 tier 的回退；实际延迟平滑映射为 reward（越快越高，非阶跃） | `800.0` |
| `ThompsonLatencyTargetMsByTier` | Per-tier 延迟目标，消除"全局单 target 系统性偏 Cheap"（强模型天生慢）。默认 `{Strong:15000, Medium:5000, Cheap:2000}` | 见左 |
| `ThompsonRaceCancelledReward` | 竞速失败（被更快模型比下去而取消）的部分奖励 `[0,1]`，默认介于慢成功 0.3 与快成功 1.0 之间 | `0.5` |
| `ThompsonLatencyNormalizeRefTokens` | 延迟归一化基准输出 token 数。`>0` 时长答案的延迟按比例宽恕，消除"长答案=慢模型"系统性惩罚；0 = 禁用 | `0` |
| `EnableContextualBandit` | 上下文老虎机（LinUCB）路由：用分类信号+tier 构造上下文特征重排，修非上下文 Thompson「只优化延迟、系统性低估 Strong」的缺陷。**与 `EnableThompsonSampling` 互斥**（启动校验强制拒绝同开） | `false` |
| `ContextualBanditAlpha` | LinUCB 探索系数 α，越大越倾向探索样本不足的模型 | `1.0` |
| `ContextualBanditDiscountFactor` | LinUCB 历史折扣因子 `[0.5, 0.99]` | `0.95` |
| `CostAwareWeight` | 成本感知权重 α ∈ [0,1]，`>0` 时 reward 混入成本项，引导学习状态偏好便宜模型。建议 0.2~0.4 | `0.0` |
| `CostAwareBaselineUsd` | 成本归一化基准（等效 $/M 混合价格），消除"长输入=贵模型"偏差；模型价格等于基准时成本 reward=0.5 | `1.0` |
| `QualityPenaltyFactor` | 低质量信号（截断/content_filter/空答）对延迟 reward 的乘性折减因子 [0,1]；1.0=不惩罚 | `0.3` |
| `EnableQualityJudge` | LLM-as-judge 采样质量打分：按采样率对非流式成功响应用打分模型评 [0,1] 分，回灌 Thompson/LinUCB。judge 调用真实计费并记审计；**会把问题与回答原文发给打分模型，隐私敏感部署保持关闭** | `false` |
| `QualityJudgeSampleRate` | judge 采样率 [0,1]，1.0=全部非流式成功请求送审。默认 0.2 控制额外成本 | `0.2` |
| `QualityJudgeModel` | judge 打分模型（路由名/`{供应商}/{Id}`/裸 Id）。留空或解析不到时静默跳过。建议 Strong 档且与被评模型不同源，避免同源自评偏置 | `null` |
| `EnableRegenerateFeedback` | regenerate 负反馈：同一规范化请求窗口内重发且上次成功 → 惩罚上次命中模型（零额外调用的质量信号；定时任务固定 prompt 场景会误判，需关闭） | `false` |
| `RegeneratePenaltyReward` | regenerate 注入的低 reward `[0.0, 1.0]`，低于慢成功地板 0.3 | `0.1` |
| `RegenerateFeedbackWindowSeconds` | regenerate 判定窗口（秒），超过窗口的同键重发视为独立请求 | `600` |
| `ExplorationEpsilon` | ε 探索保底 `[0.0, 1.0]`：段内重排后以概率 ε 把随机尾部模型提到段首，修低流量"尾部锁死"；自用建议 0.05 | `0.0` |
| `EnableLatencyAware` | 同 tier 段按历史延迟重排（快模型优先），后台聚合零 I/O | `false` |
| `LatencyMinSamples` | 延迟排序生效所需最小样本数，低于此值不参与排序 | `10` |
| `LatencyStatsWindowMinutes` | 延迟聚合统计窗口（分钟），窗口越长越平滑但响应慢 | `60` |

### 基础路由与过滤

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableRuleClassifier` | 按请求特征推断 Tier | `true` |
| `EnableTokenEstimator` | 估算 token 并过滤上下文不足的模型 | `true` |
| `EnableBudgetGuard` | 预算耗尽时执行降级/拒绝 | `true` |
| `EnableFailover` | 候选链顺序尝试，主模型失败自动切下一个 | `true` |
| `DefaultTier` | 规则分类未命中时的默认分档 | `Medium` |
| `LongInputThresholdTokens` | 超长输入阈值，超过则过滤短上下文模型 | `32000` |
| `EnableCapabilityFilter` | 按请求能力需求（vision/tool-use/json-mode）排除 Tags 不含的模型 | `false` |
| `TokenEstimation` | token 估算模式：`Tiktoken` 真实 BPE 精确计数 / `Bucket` 分桶粗估 | `Tiktoken` |
| `TiktokenEncoding` | Tiktoken 编码名（仅 `TokenEstimation=Tiktoken` 时生效） | `o200k_base` |

### 响应缓存

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableResponseCache` | 精确响应缓存（仅非流式）：按规范化请求 SHA256 缓存，命中即短路返回。缓存键在 PII 脱敏前计算 | `false` |
| `ResponseCacheTtlSeconds` | 精确缓存单条 TTL（秒），启用时必须 >0 | `3600` |
| `ResponseCacheMaxEntries` | 精确缓存最大条目数（软上限，防 OOM） | `1000` |
| `ResponseCacheMaxBytes` | 精确缓存字节预算（软上限），0=不限 | `134217728` |
| `EnableSemanticCache` | 深度语义向量响应缓存：按语义相似度匹配历史 Prompt，0 上游成本返回 | `false` |
| `SemanticCacheSimilarityThreshold` | 语义缓存命中最低余弦相似度 `[0.80, 0.99]` | `0.95` |
| `SemanticCacheTtlMinutes` | 语义缓存项生存时间（分钟） | `60` |
| `SemanticCacheMaxEntries` | 语义缓存最大条目数，超出触发 LRU/过期清理 | `10000` |

### 熔断与健康探活

| 字段 | 含义 | 默认 |
|------|------|------|
| `FailoverFailureThreshold` | 触发跨请求熔断的连续失败次数 | `3` |
| `FailoverCooldownSeconds` | 熔断冷却秒数，到期进入半开探测 | `60` |
| `FailoverGlobalTimeoutSeconds` | Failover 过程全局总超时秒数（`0` 表示不限制） | `0` |
| `FailoverHalfOpenMaxProbes` | 半开态允许的最大并发探测请求数 | `1` |
| `FailoverHalfOpenRequiredSuccesses` | 半开态连续探测成功多少次后才闭合熔断（防单次偶然成功抖动） | `1` |
| `EnableHealthProbe` | 后台主动健康探活（定时探测，结果上报断路器）。关闭则熔断恢复纯靠真实流量半开探测 | `true` |
| `HealthProbeIntervalSeconds` | 后台探活间隔秒数 | `60` |
| `HealthProbeTimeoutSeconds` | 单次探活基准超时秒数；有延迟统计的模型按平均 TTFT 自适应放宽，避免慢首 token 模型被误熔断 | `10` |
| `HealthProbeFreshSuccessSkipSeconds` | 近期成功流量新鲜窗口：真实请求/探活成功距今不足该窗口的模型跳过探活（活跃模型由真实流量背书，探活只会重复计费并引入误判）。0=始终探活 | `300` |

### 语义路由

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableSemanticRouter` | 是否启用向量空间语义路由 | `true` |
| `SemanticRouterMode` | `Hybrid`（TF-IDF 高置信短路 + 第二阶段）/ `TfIdf` / `Dense`。内置 Dense 是稳定词法特征哈希，不是训练 embedding | `Hybrid` |
| `HybridHighConfidenceThreshold` | Hybrid 模式下 TF-IDF 高置信短路阈值 | `0.45` |
| `SemanticSimilarityThreshold` | 语义匹配余弦相似度阈值 `[0.0, 1.0]`，低于此值不命中 | `0.25` |
| `SemanticRoutes` | 语义路由规则列表，每条含 `Name`/`TargetTier`/`Phrases` | `[]` |
| `EnableOnnxEmbedding` | 本地 ONNX 轻量级向量模型（如 bge-small-zh）替换默认词法特征哈希 | `false` |
| `OnnxModelPath` | 本地 ONNX 模型文件路径 | `null` |
| `OnnxExecutionProvider` | ONNX 执行提供者：`CPU` 或 `CUDA` | `CPU` |

### 亲和与配额

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableSessionAffinity` | 显式 `X-Session-Id` 会话粘性 | `false` |
| `SessionAffinityTtlSeconds` | 会话粘性 TTL（秒） | `600` |
| `EnablePromptCacheAffinity` | 稳定前缀缓存粘性：仅保存 SHA-256 指纹，软提升上次成功模型 | `false` |
| `PromptCacheAffinityTtlSeconds` | 稳定前缀指纹粘性 TTL（秒，必须 > 0） | `600` |
| `EnableQuotaAwareRouting` | 读取进程内上游配额快照，软降级低余量并在已知 reset 窗口内排除耗尽模型 | `false` |

### Fusion / 竞速 / 流式

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableFusionMode` | 并行首试（Fusion-lite/Race）：非流式首轮并行前 N 候选取最快成功，取消其余 | `false` |
| `EnableFusionRouter` | **融合路由**（OpenRouter Fusion 式）：非流式/流式首轮并行 panel → analyst 结构化分析 → outer 写最终答案。质量技术，成本 N+2 调用（N=panel 数）。**默认关闭，需显式启用并承担成本** | `false` |
| `FusionRouterPanelSize` | 融合路由 panel 并行模型数，范围 `[2, 5]` | `3` |
| `EnableDynamicFusionPanelSize` | 按 typed request complexity 在最小/最大范围内动态选 panel 数 | `false` |
| `FusionRouterMinPanelSize` | 动态 Fusion panel 最小数，范围 `[2, 5]` 且不得大于 `FusionRouterPanelSize` | `2` |
| `EnableFusionDiversity` | 软优先不同 `Provider`/`Family`，元数据不足时按原候选顺序补齐 | `false` |
| `FusionRouterAnalystModel` | 融合路由 analyst 模型名（留空=主候选）；只产结构化 JSON | `null` |
| `FusionRouterAnalystPrompt` | 融合路由 analyst 专用 JSON 分析提示词（留空=内置提示词） | `null` |
| `FusionRouterOuterModel` | 融合路由 outer 模型名（留空=主候选）；读分析写最终答案 | `null` |
| `FusionRouterMaxOutputTokens` | 融合路由 outer 答案最大输出 token 数 | `16000` |
| `FusionRouterTemperature` | 融合路由 panel/analyst 采样温度，范围 `[0, 2]` | `0.0` |
| `FusionRouterPanelTemperature` | panel 专用采样温度；`null`=沿用 `FusionRouterTemperature` | `null` |
| `FusionRouterMinComplexity` | 融合路由最低复杂度门控（`Unknown`/`Simple`/`Standard`/`Complex`） | `Unknown` |
| `StreamFirstTokenTimeoutMs` | 流式首 token（TTFB）超时毫秒数。`0` 表示不限制 | `0` |
| `StreamHedgeDelayMs` | 流式首行竞速（Hedge）延迟毫秒数。`>0` 且存在下一候选时，主候选开始拉流后延迟启动下一候选竞速首行，谁先出首行由谁服务（落败方取消并按慢首行记失败；已生成部分可能仍被上游计费）。`0` = 禁用 | `0` |
| `MaxResponseStreamBytes` | 流式响应累计字节硬上限，防 OOM/恶意无限流 | `20971520`(20MB) |

### 合规 / 可观测 / 审计

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnablePiiAnonymization` | PII 敏感数据脱敏与反向还原（手机/邮箱/身份证/卡号/IP）。**隐私敏感部署建议启用** | `false` |
| `EnableDataSovereignty` | 数据不出域隔离屏障（强制仅路由至本地/私有节点）。**合规部署建议启用** | `false` |
| `EnablePersonaDriftProtection` | 多轮对话人设一致性防护（静态人设锚点提示词） | `false` |
| `EnableJsonAstAutoRepair` | JSON AST 自动修补（剥离代码围栏、修复逗号、截断补全） | `true` |
| `EnableDistributedTracing` | W3C 分布式链路追踪（TraceId/SpanId，映射 ActivitySource） | `true` |
| `EnableOtlpTracing` | 原生 OpenTelemetry OTLP Exporter 导出 DAG 链路追踪 | `false` |
| `OtlpEndpoint` | OTLP 接收端点 | `"http://localhost:4317"` |
| `OtlpProtocol` | OTLP 传输协议：`grpc` 或 `http/protobuf` | `"grpc"` |
| `OtlpServiceName` | OpenTelemetry 导出的服务名称 | `"OptiRouter"` |
| `EnableMetrics` | Prometheus `/metrics` 端点（仅聚合数+模型名） | `true` |
| `MetricsEndpointPath` | 指标端点路径 | `/metrics` |
| `MetricsApiKey` | `/metrics` 端点鉴权密钥（Bearer）。null 保持无鉴权；公网部署建议配置 | `null` |
| `AuditStoreRequestContent` | 审计库与 Dashboard 是否留存请求内容明文（管理员可显式 opt-in。升级注意：该默认值由早前版本的 `true` 改为 `false`） | `false` |
| `AuditRetentionHours` | 审计记录保留小时数。`0` = 永久保留（默认）；正数按窗口周期淘汰，防审计表无界增长 | `0` |

## 管理台角色（最小 RBAC）

| 角色 | 能力 |
|------|------|
| `viewer` | 全部管理 API 只读（指标/审计/分析/评测历史/状态/配置读取）；**例外（仅 admin）**：上游密钥明文查看（`/api/models/*/apikey`）、管理身份列表（`/api/dashboard/identities`） |
| `operator` | viewer 之外另可：沙箱试路由、评测运行/对比、学习状态重置、熔断手工覆写、模型连通性测试/发现、PUT 路由配置与语义路由 |
| `admin` | 全部能力：租户 Key 管理、模型配置写、管理身份签发/撤销，及一切未显式归入 operator 的写操作（安全默认） |

主密钥（`AdminApiKey` 种子/登录）恒为 admin。附加身份经 `POST /api/dashboard/identities` 签发（`{name, role}`，明文密钥仅返回一次，库内只存 SHA256 哈希与前缀），`DELETE /api/dashboard/identities/{id}` 撤销后密钥立即失效；身份列表查询同样仅 admin。执法单点在中间件：管理 API 认证成功后按 `AdminOperations.Can` 矩阵判定，越权返回裸 403；新增写端点若未显式归入 operator 白名单，默认仅 admin 可用（见 [AdminRole.cs](../src/OptiRouter/Security/AdminRole.cs) 注释）。

已决策口径（2026-09-30）：① 明文密钥查看按「`/api/models` 下以 `/apikey` 结尾」宽收口，别名路由同拦；② 管理身份列表对 viewer/operator 均不可见（管理账号构成不对下位角色暴露）；③ 执法只在 API 层，页面级不做角色拦截（导航按角色隐藏，直接访问页面不越权、写操作 403），属有意取舍。
