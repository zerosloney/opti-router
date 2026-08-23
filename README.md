# OptiRouter

多模型智能路由 HTTP 代理（.NET 8）。OpenAI 兼容接口，自动选模型，省 Token 降成本，自带数据合规屏障、分布式 DAG 链路追踪与端云投机解码编排。

## 架构

```
┌──────────┐   POST /v1/chat/completions   ┌──────────────┐
│  Client  │ ────────────────────────────▶ │ OptiRouter   │
│ (OpenAI  │                               │   (ASP.NET   │
│  SDK /   │ ◀─────────────────────────── │    Core)     │
│  curl)   │   非流式 JSON / SSE 流式      └──────┬───────┘
└──────────┘                                      │
                                          RouterEngine.Decide
                                                  │
                   ┌──────────────────────────────┴──────────────┐
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ RuleClassif │ → Tier(Strong/Medium/Cheap)    │ TokenEstimator│ → 长输入过滤
            └─────────────┘                               └───────────────┘
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ DataSovereig│ → 数据不出域/私有节点隔离      │ PiiAnonymizer │ → PII 脱敏/还原
            └─────────────┘                               └───────────────┘
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ BudgetGuard │ → 预算耗尽降级/拒绝            │ FailoverPolicy│ → 排除失败模型
            └─────────────┘                               └───────────────┘
                                          候选链 [A, B, C]
                                                  │
                                   ProxyOrchestrator / Race / Fusion
                                                  │
             ┌────────────────────────────────────┼────────────────────────────────────┐
             ▼                                    ▼                                    ▼
      ┌────────────┐                       ┌────────────┐                       ┌────────────┐
      │  Model A   │                       │  Model B   │                       │  Model C   │  (OpenAI 兼容上游)
      └────────────┘                       └────────────┘                       └────────────┘
```

## 核心特性

- 🤖 **auto 虚拟模型与显式固定路由**：`GET /v1/models` 首位暴露虚拟模型 `auto`——请求 `model="auto"` 或缺省时走全链路智能路由；真实模型以 `{供应商}/{真实模型Id}` 格式展示（如 `deepseek/deepseek-chat`，同供应商同模型多 Key 追加 ` #2`），请求该格式 id 时自动解析并转换为上游内部模型 ID 发送，也接受路由名或裸模型 Id（多端点提供同一模型时固定为提供方集合，路由器在其中择优/降级）；仅数据合规/预算/熔断等硬约束可否决，不静默换模型，未知模型名按 OpenAI 兼容语义返回 404 `model_not_found`。模型配置的 `Id` 对应上游真实请求模型（如 `deepseek-chat`），`Name` 留空时自动生成为「供应商/模型」。
- 🚀 **渐进式投机流 (Progressive Speculative Streaming)**：融合路由流式输出首字延迟设计目标显著低于全流程融合，Anchor 节点即时推流，背景 Panel 模型与 Analyst 异步分析增量 Patch 补丁。
- ⚡ **Prompt Cache 蒸馏 & APC 自动对齐**：Panel 文本蒸馏过滤无用废话（历史轮次折叠、去重、填充语剔除，实际节省取决于对话结构），Top-loaded 静态前缀（`[SYSTEM_PREFIX_INSTRUCTION]`）实现 Automatic Prefix Caching 高高效对齐。
- 🛡️ **P1 合规防护与 JSON AST 自动修复**：
  - **PII 脱敏与还原**：自动识别手机、邮箱、身份证号、银行卡号与 IP 地址，双向占位符还原。（默认关闭，需显式启用 `EnablePiiAnonymization=true`）
  - **数据不出域屏障**：开启 `EnableDataSovereignty` 强制过滤外部云端节点，仅路由至私有/本地端点 (`IsLocalOrPrivate`)。（默认关闭，需显式启用 `EnableDataSovereignty=true`）
  - **JSON AST 容错修复**：自动剥离 Markdown 代码围栏、清除控制字符、修补尾部非法逗号，并自动补全因 `MaxTokens` 截断导致的缺失括号。
- 🔍 **P2 分布式 DAG 链路追踪与 Persona 锁**：
  - **W3C 规范链路追踪**：支持 `traceparent` 解析与 ActivitySource 映射，多模型 Panel/Analyst/Outer 结构化 DAG 树成本分拆归因。
  - **人设一致性防护 (`PersonaDriftGuard`)**：自动植入静态人设锚点提示词，配合 Session 粘性锁防止多轮 Agent 对话 Persona 漂移。
- 🧪 **P3 提示词版本化与端云投机解码**：
  - **提示词版本管理 (`PromptTemplateManager`)**：Analyst / Outer 系统提示词模版版本控制与变量动态插值。（规划中，尚未实现）
  - **Golden Dataset 离线回归评测 (`OfflineEvalRunner`)**：自动化 Golden Question 题库 Jaccard 词重叠相似度、准确率、延迟与 Token 消耗回归报告。
  - **端云混合投机解码 (`HybridSpeculativeOrchestrator`)**：本地 1B/3B 端侧模型极速生成 Draft 草稿，云端强模型（Verifier）二次校验修补，兼顾高智力与低开支。
- 🏎️ **0-阻塞高性能架构**：
  - **ConcurrentQueue 异步批处理落盘**：请求完成 1 微秒入列，后台批量事务落库（SQLite/MariaDB），主数据平面 0 I/O 阻塞。
  - **Monitor.TryEnter 非阻塞限流 Sweeper**：并发清理锁 0 阻断 HTTP 请求管道。
  - **MemoryCache SizeLimit 内存保护**：硬顶淘汰防范恶意 SessionId 膨胀攻击。

## 快速开始

### 前置要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 构建

SDK 版本由根目录 `global.json` 固定（当前 8.0.303 + rollForward）。

```bash
dotnet build OptiRouter.sln -c Release
```

### 配置

1. 复制 `appsettings.example.json` 为 `appsettings.json`
2. 填入真实 ApiKey，或使用环境变量覆盖：

```bash
# Windows PowerShell
$env:OptiRouter__Models__0__ApiKey = "sk-..."

# Linux / macOS
export OptiRouter__Models__0__ApiKey="sk-..."
```

- **代理鉴权（租户 key）**：`/v1/*` 请求使用租户 Client Key（管理台 Keys 页创建，
  按 key 独立限 QPS/日预算）。全局 `ProxyApiKey` 已移除。
- **管理密钥（AdminApiKey）**：SHA256 哈希存配置库（首次启动时 appsettings/环境变量中的
  `OptiRouter:AdminApiKey` 作为种子哈希入库后即被忽略；两者皆缺则生成随机密钥并打印
  启动日志一次）。轮换 = 清空配置库 `security` scope 后重启。

### 运行

```bash
dotnet run --project src/OptiRouter
```

应用默认监听 `http://localhost:5000`。

### 健康检查

```bash
curl http://localhost:5000/health
```

`/health` 无需 API Key，且不受请求限流影响。

## 配置说明

`appsettings.json` 中 `OptiRouter` 节点各字段含义：

### 入站安全

| 字段 | 含义 | 默认 |
|------|------|------|
| `ProxyApiKey` | 调用 `/v1/*` 与 `/dashboard`、`/api/dashboard/*` 时使用的 Bearer API Key；为空时拒绝访问 | 空 |
| `RequestsPerMinute` | 每个分区（IP > Auth）的固定窗口每分钟请求上限 | `60` |
| `MaxConcurrentRequestsPerPartition` | 每个分区同时进行的最大请求数，超出返回 429 | `100` |

> **分区 Key 优先级**：客户端 IP（启用 `TrustProxyHeaders` 时依次使用 `CF-Connecting-IP`、`X-Forwarded-For` 首段，否则使用 `RemoteIpAddress`）> Bearer Token（SHA256 哈希前 16 hex）。仅当无法取得 IP 时按认证标识分区；`X-Session-Id` 不参与限流分区。

### Models[]（模型端点列表）

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

### Budget（预算控制）

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

### Routing（路由策略）

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableRuleClassifier` | 按请求特征推断 Tier | `true` |
| `EnableTokenEstimator` | 估算 token 并过滤上下文不足的模型 | `true` |
| `EnableBudgetGuard` | 预算耗尽时执行降级/拒绝 | `true` |
| `EnableFailover` | 候选链顺序尝试，主模型失败自动切下一个 | `true` |
| `EnablePiiAnonymization` | 是否启用 PII 敏感数据脱敏与反向还原（手机/邮箱/身份证/卡号/IP）。**默认关闭，隐私敏感部署建议启用** | `false` |
| `EnableDataSovereignty` | 是否启用数据不出域隔离屏障（强制仅路由至本地/私有节点）。**默认关闭，合规部署建议启用** | `false` |
| `EnableJsonAstAutoRepair` | 是否启用 JSON AST 自动化修补服务（剥离代码围栏、修复逗号、截断补全） | `true` |
| `EnableDistributedTracing` | 是否启用 W3C 分布式链路追踪（生成 TraceId/SpanId，映射 ActivitySource） | `true` |
| `EnablePersonaDriftProtection` | 是否启用多轮对话人设一致性防护（静态人设锚点提示词）。**默认关闭** | `false` |
| `LongInputThresholdTokens` | 超长输入阈值，超过则过滤短上下文模型 | `32000` |
| `DefaultTier` | 规则分类未命中时的默认分档 | `Medium` |
| `TokenEstimation` | token 估算模式：`Tiktoken` 真实 BPE 精确计数 / `Bucket` 分桶粗估 | `Tiktoken` |
| `TiktokenEncoding` | Tiktoken 编码名（仅 `TokenEstimation=Tiktoken` 时生效） | `o200k_base` |
| `FailoverFailureThreshold` | 触发跨请求熔断的连续失败次数 | `3` |
| `FailoverCooldownSeconds` | 熔断冷却秒数，到期进入半开探测 | `60` |
| `FailoverGlobalTimeoutSeconds` | Failover 过程全局总超时秒数（`0` 表示不限制；超过此时间终止候选重试） | `0` |
| `FailoverHalfOpenMaxProbes` | 半开态允许的最大并发探测请求数 | `1` |
| `FailoverHalfOpenRequiredSuccesses` | 半开态连续探测成功多少次后才闭合熔断（防单次偶然成功导致抖动） | `1` |
| `EnableHealthProbe` | 是否启用后台主动健康探活（定时对所有启用模型探测，结果上报断路器） | `true` |
| `HealthProbeIntervalSeconds` | 后台探活间隔秒数 | `60` |
| `EnableSemanticRouter` | 是否启用向量空间语义路由 | `true` |
| `SemanticRouterMode` | `Hybrid`（TF-IDF 高置信短路 + 第二阶段）/ `TfIdf` / `Dense` | `Hybrid` |
| `EnableOnnxEmbedding` | 是否启用本地 ONNX 轻量级向量模型（如 bge-small-zh / all-MiniLM-L6-v2）进行深层隐式语义路由 | `false` |
| `OnnxModelPath` | 本地 ONNX 模型文件路径（如 `models/bge-small-zh.onnx`） | `null` |
| `OnnxExecutionProvider` | ONNX 执行提供者：`CPU` 或 `CUDA` | `CPU` |
| `HybridHighConfidenceThreshold` | Hybrid 模式下 TF-IDF 高置信短路阈值；低于阈值交给第二阶段判定 | `0.45` |
| `SemanticSimilarityThreshold` | 语义匹配余弦相似度阈值 `[0.0, 1.0]`，低于此值不命中 | `0.25` |
| `SemanticRoutes` | 语义路由规则列表，每条含 `Name`/`TargetTier`/`Phrases` | `[]` |
| `EnableSessionAffinity` | 显式 `X-Session-Id` 会话粘性 | `false` |
| `SessionAffinityTtlSeconds` | 会话粘性 TTL（秒） | `600` |
| `EnablePromptCacheAffinity` | 稳定前缀缓存粘性：仅保存 SHA-256 指纹，软提升上次成功模型 | `false` |
| `PromptCacheAffinityTtlSeconds` | 稳定前缀指纹粘性 TTL（秒，必须 > 0） | `600` |
| `EnableQuotaAwareRouting` | 读取进程内上游配额快照，软降级低余量并在已知 reset 窗口内排除耗尽模型 | `false` |
| `MaxResponseStreamBytes` | 流式响应累计字节硬上限，防 OOM/恶意无限流 | `20971520`(20MB) |
| `EnableCascadeUpgrade` | Cheap→Strong 级联自校验（采样，低置信升级重答） | `false` |
| `CascadeUpgradeSampleRate` | 级联采样率 `[0.0, 1.0]`，0=关闭，1=全量 | `0.1` |
| `CascadeUpgradeVerifierModel` | 级联校验模型名（他评，消除"模型自评"自利偏差）；留空=自评，建议填 Strong 模型 | `null` |
| `EnableRegenerateFeedback` | regenerate 负反馈：同一规范化请求窗口内重发且上次成功 → 惩罚上次命中模型（零额外调用的质量信号；定时任务固定 prompt 场景会误判，需关闭） | `false` |
| `RegeneratePenaltyReward` | regenerate 注入的低 reward `[0.0, 1.0]`，低于慢成功地板 0.3 | `0.1` |
| `RegenerateFeedbackWindowSeconds` | regenerate 判定窗口（秒），超过窗口的同键重发视为独立请求 | `600` |
| `ExplorationEpsilon` | ε 探索保底 `[0.0, 1.0]`：段内重排后以概率 ε 把随机尾部模型提到段首，修低流量"尾部锁死"；自用建议 0.05 | `0.0` |
| `EnableLatencyAware` | 同 tier 段按历史延迟重排（快模型优先），后台聚合零 I/O | `false` |
| `LatencyMinSamples` | 延迟排序生效所需最小样本数，低于此值不参与排序 | `10` |
| `LatencyStatsWindowMinutes` | 延迟聚合统计窗口（分钟），窗口越长越平滑但响应慢 | `60` |
| `EnableCapabilityFilter` | 按请求能力需求（vision/tool-use/json-mode）排除 Tags 不含的模型 | `false` |
| `EnableFusionMode` | 并行首试：非流式首轮并行前 N 候选取最快成功，取消其余 | `false` |
| `FusionMaxParallel` | 并行首试首轮并发数，范围 `[2, 5]` | `2` |
| `EnableFusionRouter` | **融合路由**（OpenRouter Fusion 式）：非流式/流式首轮并行 panel → analyst 结构化分析 → outer 写最终答案。质量技术，成本 N+2 调用（N=panel 数）。**默认关闭，需显式启用并承担成本** | `false` |
| `FusionRouterPanelSize` | 融合路由 panel 并行模型数，范围 `[2, 5]` | `3` |
| `EnableDynamicFusionPanelSize` | 按 typed request complexity 在最小/最大范围内动态选 panel 数；不解析 reason 文本 | `false` |
| `FusionRouterMinPanelSize` | 动态 Fusion panel 最小数，范围 `[2, 5]` 且不得大于 `FusionRouterPanelSize` | `2` |
| `EnableFusionDiversity` | 软优先不同 `Provider`/`Family`，元数据不足时按原候选顺序补齐 | `false` |
| `FusionRouterAnalystModel` | 融合路由 analyst 模型名（留空=主候选）；只产结构化 JSON | `null` |
| `FusionRouterAnalystPrompt` | 融合路由 analyst 专用 JSON 分析提示词（留空=内置提示词） | `null` |
| `FusionRouterOuterModel` | 融合路由 outer 模型名（留空=主候选）；读分析写最终答案 | `null` |
| `FusionRouterMaxOutputTokens` | 融合路由 outer 答案最大输出 token 数 | `16000` |
| `FusionRouterTemperature` | 融合路由 panel/analyst 采样温度，范围 `[0, 2]` | `0.0` |
| `FusionRouterPanelTemperature` | panel 专用采样温度；`null`=沿用 `FusionRouterTemperature` | `null` |
| `FusionRouterMinComplexity` | 融合路由最低复杂度门控（`Unknown`/`Simple`/`Standard`/`Complex`） | `Unknown` |
| `EnableOnnxEmbedding` | 启用本地 ONNX Transformer 轻量级 Embedding 深度语义向量路由引擎 | `false` |
| `OnnxModelPath` | ONNX 模型文件绝对路径或相对路径（如 `"data/all-MiniLM-L6-v2.onnx"`） | `"data/all-MiniLM-L6-v2.onnx"` |
| `OnnxExecutionProvider` | ONNX 执行提供者，可选 `"CPU"` 或 `"CUDA"` | `"CPU"` |
| `EnableOtlpTracing` | 启用原生 OpenTelemetry OTLP Exporter 导出 ActivitySource DAG 链路追踪 | `false` |
| `OtlpEndpoint` | OTLP Exporter 接收端点（如 `"http://localhost:4317"`） | `"http://localhost:4317"` |
| `OtlpProtocol` | OTLP 传输协议：可选 `"grpc"` 或 `"http/protobuf"` | `"grpc"` |
| `OtlpServiceName` | OpenTelemetry 导出的服务名称 | `"OptiRouter"` |
| `EnableMetrics` | 启用 Prometheus `/metrics` 端点（无鉴权，仅聚合数+模型名） | `true` |
| `MetricsEndpointPath` | 指标端点路径 | `/metrics` |
| `MetricsApiKey` | `/metrics` 端点鉴权密钥（Bearer Token）。非空时要求 `Authorization: Bearer <key>`；null 保持无鉴权 | `null` |
| `AuditStoreRequestContent` | 审计库与 Dashboard 是否留存请求内容明文（默认关闭；管理员可显式设为 `true` 以 opt-in。升级注意：该默认值由早前版本的 `true` 改为 `false`，依赖请求内容留存的部署需显式开启） | `false` |
| `AuditRetentionHours` | 审计记录保留小时数。`0` = 永久保留（默认，后台不淘汰）；正数按窗口周期淘汰过期记录，防止审计表无界增长 | `0` |
| `StreamFirstTokenTimeoutMs` | 流式首 token（TTFB）超时毫秒数。`0` 表示不限制，仅依赖客户端层超时兜底 | `0` |

### 推荐配置预设 (Presets)

预设是起点不是终点。你可以：

1. **粘贴完整 JSON 片段**到 `appsettings.json` 的 `"OptiRouter"` 节点后，根据实际需求微调单个开关。显式配置的 key 会覆盖默认值。
2. **仅使用预设名称**：在 `"OptiRouter"` 节点设置 `"Routing": { "Preset": "balanced" }`，preset 仅填充未显式配置的项、显式配置优先、仅覆盖 Routing 节（Budget 需单独设）。

**两种方式等价，后者更简洁。**

```json
{
  "OptiRouter": {
    "Routing": { ... },
    "Budget": { ... }
  }
}
```

#### 1. cost-first（成本优先——批量/离线/高流量）

```json
{
"Routing": {
  "EnableThompsonSampling": true,
  "EnableLatencyAware": true,
  "ExplorationEpsilon": 0.05,
  "EnableResponseCache": true,
  "DefaultTier": "Cheap"
},
"Budget": {
  "EnforceOnExhausted": "Degrade"
}
}
```

**适用场景**：批量数据处理、离线任务、高流量简单查询场景。

**行为说明**：
- 启用 Thompson 采样与延迟感知路由，系统自动学习并收敛到延迟低且成本低的模型（高频请求路径优化明显）
- 5% ε 探索保底确保尾部模型仍有流量样本，低流量实例收益有限
- 响应缓存对重复问题精确去重（幂等请求零成本，命中即短路返回）
- 预算耗尽时自动降档到更便宜的模型继续服务（拒绝成本高于降级风险）
- 对单次回答质量不敏感，追求总体吞吐与成本最优

#### 2. balanced（均衡——通用对话/Agent 后端，推荐起点）

```json
{
"Routing": {
  "EnableThompsonSampling": true,
  "EnableCascadeUpgrade": true,
  "CascadeUpgradeSampleRate": 0.1,
  "EnableResponseCache": true,
  "DefaultTier": "Medium"
}
}
```

**适用场景**：通用 Chatbot、Agent 后端、多轮对话场景（生产环境推荐起点）。

**行为说明**：
- Thompson 采样让系统根据历史延迟与成功率自适应调整模型选择
- 开启 10% 采样的 Cheap→Strong 级联自校验：简单问题由 Cheap 模型直接回答并自评置信度，低置信时升级 Strong 模型重答（质量漏洞兜底）
- 建议配置 `"CascadeUpgradeVerifierModel": "某个Strong模型名"` 消除"模型自评"的自利偏差，他评可信度更高
- 响应缓存去重重复提问，减少不必要的模型调用
- 中档模型作为默认起点，平衡成本与质量

#### 3. quality-first（质量优先——高风险低流量）

```json
{
"Routing": {
  "DefaultTier": "Strong",
  "EnableFusionRouter": true,
  "EnableByzantineConsensus": true,
  "EnableCascadeUpgrade": true,
  "CascadeUpgradeSampleRate": 0.3
},
"Budget": {
  "EnforceOnExhausted": "Reject"
}
}
```

**适用场景**：高风险决策、金融/医疗诊断、复杂推理任务（低流量可容忍高成本）。

**行为说明**：
- 默认直接路由到 Strong 档模型（最强能力档）
- 融合路由并行调用多个 Panel 模型作答 → Analyst 结构化分析共识/矛盾/缺口 → Outer 写最终答案（成本约 N+2 次调用，N=Panel 数）
- 拜占庭共识在 Panel 输出高度一致时直接采纳多数派（捷径命中时降为 N 次调用），分歧时交给 Analyst 深度仲裁
- **注意**：`EnableByzantineConsensus` 仅在 `EnableFusionRouter` 开启的非流式融合路径生效
- 30% 级联采样率（高于 balanced 的 10%）加强质量兜底
- 预算耗尽时宁可拒绝请求也不降档（质量不可妥协）

### 分布式存储与多节点 K8s 部署 (Kubernetes Multi-Node Deployment)

对于无状态多节点 Kubernetes 部署，OptiRouter 提炼了抽象存储接口 `ICostLedgerStore` 与 `IRequestAuditStore`；PostgreSQL 可跨节点共享成本账本、断路器状态与请求审计汇总，Redis 仅共享成本账本与断路器状态：

```json
{
  "OptiRouter": {
    "Budget": {
      "StoreProvider": "Redis", // 可选 "Sqlite" | "Postgres" | "Redis" | "InMemory"
      "RedisConnectionString": "localhost:6379,abortConnect=false",
      "RedisKeyPrefix": "optirouter:",
      "PostgresConnectionString": "Host=localhost;Database=optirouter;Username=postgres;Password=secret"
    }
  }
}
```

## 管理控制台（Dashboard Console）

Blazor Server 管理台（`/overview` `/requests` `/models` `/router` `/keys` `/benchmarks`），登录会话或 `AdminApiKey` Bearer 鉴权。配置类操作写入配置库（SQLite 或 MariaDB）并触发热重载，无需重启。

| 能力 | 说明 |
|------|------|
| 告警历史与 Webhook 订阅 | 告警出现/恢复事件进程内留痕（200 条环形缓冲，Overview 展示）；`AlertWebhookUrl` / `AlertWebhookIntervalSeconds` 在路由页「告警订阅」组编辑，保存后热生效——未配置 URL 时仍记录历史，配置后即推送（无需重启） |
| 配置变更审计 | 路由/预算配置每次落库自动记录 key 级 diff（如 `Routing:EnableFailover: true → false`），保留最近 200 条，路由页「配置变更历史」卡片可查 |
| 请求审计筛选/搜索/导出 | 审计日志支持按 Request/Trace ID 子串搜索与 UTC 时间范围过滤，可导出 CSV（`GET /api/dashboard/requests/export`，同筛选条件） |
| 租户 Key 用量与导出 | Keys 页展示每个租户的今日消费、用量占比（≥80% 变红）、剩余预算与请求数；`GET /api/dashboard/keys/usage/export` 导出 CSV；删除 Key 需二次确认 |
| 模型能力标签 | 模型弹窗编辑 `Tags`（`vision` / `tool-use` / `json-mode`，逗号分隔自动去重），列表显示标签芯片；配合路由页「能力过滤」开关按能力硬过滤候选 |
| 评测批次持久化 | Golden Dataset 评测报告落配置库（保留最近 10 批），重启不丢失，A/B 对比跨重启可用 |
| 学习状态管理 | Thompson / Contextual Bandit 状态可一键重置为初始先验（含持久化回落，需确认）或导出 CSV |
| Fusion 编排参数 | 面板规模（数量/动态/最小/多样性）、Analyst/Outer 模型下拉、采样与预算（最大输出/温度/Panel 超时）、Analyst 提示词、竞速参数（并发数/Hedge 延迟）全部可在路由页编辑并热生效 |

## curl 示例

非流式：

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer your-proxy-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "messages": [{"role": "user", "content": "解释什么是多态"}],
    "stream": false
  }'
```

流式（支持渐进式投机流推流）：

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer your-proxy-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "messages": [{"role": "user", "content": "写个快排"}],
    "stream": true
  }'
```

> 说明：`model` 字段会被路由器忽略——模型由路由策略决定；传任何值都行。

### 多协议入口（协议对齐）

除 OpenAI 格式外，也接受 Anthropic 与 Gemini 原生协议，三种入口共用同一套路由、预算、熔断与审计：

```bash
# Anthropic Messages API（鉴权：Authorization: Bearer 或 x-api-key）
curl -X POST http://localhost:5000/v1/messages \
  -H "x-api-key: your-proxy-api-key" \
  -H "anthropic-version: 2023-06-01" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "max_tokens": 1024,
    "messages": [{"role": "user", "content": "解释什么是多态"}]
  }'

# Gemini generateContent（鉴权：Authorization: Bearer、x-goog-api-key 或 ?key=）
curl -X POST "http://localhost:5000/v1beta/models/auto:generateContent" \
  -H "x-goog-api-key: your-proxy-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "contents": [{"role": "user", "parts": [{"text": "解释什么是多态"}]}]
  }'
```

流式分别走 `"stream": true`（Anthropic 事件序列）与 `:streamGenerateContent?alt=sse`（Gemini SSE 块）。`auto` 语义与模型校验同 OpenAI 入口一致；文本、system、工具调用（tool_use/tool_result 与 functionCall/functionResponse）双向翻译。

## 测试

### 单元测试与集成测试

运行全量 1000+ 项单元与集成测试套件（随迭代增长）：

```bash
dotnet test OptiRouter.sln -c Release
```

### 端到端冒烟测试

使用 WireMock.Net 起真实 HTTP mock server，验证完整链路（HTTP 入 → 路由 → 真实 HTTP 出 → WireMock 响应 → 回传）。

```bash
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~EndToEndSmokeTests"
```

## 审计分析

审计库（SQLite 或 MariaDB 后端，见 Budget.StoreProvider / ConfigDbConnectionString）按时间窗全量聚合，产出策略调优闭环的实证依据——各模型/分档实际成功率、成本分布、延迟分位（P50/P95/P99）、级联触发率、Fusion 角色分布、路由原因 Top N 与按日趋势。

分析能力内置于服务（`AuditAnalysisService`），对所有存储后端（InMemory/SQLite/MariaDB/Postgres）通用，经管理 API 获取 JSON 报告：

```bash
# 最近 24 小时窗口（from/to 为 UTC ISO 时间，需 AdminApiKey）
curl -H "Authorization: Bearer <AdminApiKey>" \
    "http://localhost:5080/api/dashboard/audit/analysis?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z"
```

报告结构：`summary`（总量/成功率/成本/Token/延迟分位）、`byModel`、`byTier`（含成本份额）、`cascade`（触发率 + 升级来源分布）、`fusion`、`byReason`（Top 20）、`dailyTrend`。

## 部署

### Docker（推荐容器化部署）

多阶段构建（`sdk:8.0` 编译 → `aspnet:8.0` 运行），非 root 用户运行，内建 `HEALTHCHECK`。

```bash
# 构建镜像
docker build -t optirouter .

# 运行（存储走 MariaDB：配置库 + 租户 Key + 成本账本 + 审计 + 学习状态，
# 只配一处连接即可——StoreProvider 默认 Auto 自动选择；多实例共享同一库即为全局口径；
# 首启 DB 为空时按 appsettings/环境变量播种一次）
docker run -d --name optirouter \
  -p 5000:5000 \
  -e OptiRouter__ProxyApiKey="your-proxy-api-key" \
  -e OptiRouter__AdminApiKey="your-admin-api-key" \
  -e OptiRouter__ConfigDbConnectionString="Server=mariadb;Port=3306;Database=optirouter;User ID=optirouter;Password=..." \
  optirouter

# 或最小化运行（不配 DB 时回退 SQLite 文件，挂载 /app/data 卷持久化）
# docker run -d --name optirouter -p 5000:5000 \
#   -v optirouter-data:/app/data \
#   -e OptiRouter__ProxyApiKey="..." -e OptiRouter__AdminApiKey="..." optirouter
```

## 运维备忘

- **日志**：Serilog 滚动文件 `logs/service-yyyyMMdd.log`——按天分文件、单文件 50MB 上限、自动保留最近 14 个（约 700MB 封顶后淘汰最旧），无需人工清理。启动早期的控制台输出（Serilog 初始化前）重定向在 `logs/boot.log`（`start-local.cmd`）。
- **配置库备份**：配置库（MariaDB `optirouter_*` 表 / SQLite `data/optirouter-config.db`）是路由、预算、模型与租户 Key 的唯一权威，建议纳入例行备份：
  - MariaDB：`mysqldump -h127.0.0.1 -uroot -p test optirouter_app_config optirouter_client_keys > optirouter-config-backup.sql`
  - SQLite：直接复制 `data/optirouter-config.db`（服务停止时，或用 `.backup` 语义的工具）
- **安全加固**（公网部署前）：设置强随机 `AdminApiKey`；`TrustProxyHeaders=true` 仅在可信反代之后开启；`OptiRouter:Routing:MetricsApiKey` 建议配置以免 `/metrics` 裸露；管理 API 的 Bearer 失败尝试与登录页共享同一 IP 锁定窗口（5 次失败锁 5 分钟）。

## 许可证

[MIT License](LICENSE)
