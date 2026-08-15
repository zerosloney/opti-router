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
- 🚀 **渐进式投机流 (Progressive Speculative Streaming)**：融合路由流式输出首字延迟 TTFT < 200ms，Anchor 节点即时推流，背景 Panel 模型与 Analyst 异步分析增量 Patch 补丁。
- ⚡ **Prompt Cache 蒸馏 & APC 自动对齐**：Panel 文本蒸馏过滤无用废话（节省 50%~70% Token），Top-loaded 静态前缀（`[SYSTEM_PREFIX_INSTRUCTION]`）实现 Automatic Prefix Caching 高高效对齐。
- 🛡️ **P1 合规防护与 JSON AST 自动修复**：
  - **PII 脱敏与还原**：自动识别手机、邮箱、身份证号、银行卡号与 IP 地址，双向占位符还原。
  - **数据不出域屏障**：开启 `EnableDataSovereignty` 强制过滤外部云端节点，仅路由至私有/本地端点 (`IsLocalOrPrivate`)。
  - **JSON AST 容错修复**：自动剥离 Markdown 代码围栏、清除控制字符、修补尾部非法逗号，并自动补全因 `MaxTokens` 截断导致的缺失括号。
- 🔍 **P2 分布式 DAG 链路追踪与 Persona 锁**：
  - **W3C 规范链路追踪**：支持 `traceparent` 解析与 ActivitySource 映射，多模型 Panel/Analyst/Outer 结构化 DAG 树成本分拆归因。
  - **人设一致性防护 (`PersonaDriftGuard`)**：自动植入静态人设锚点提示词，配合 Session 粘性锁防止多轮 Agent 对话 Persona 漂移。
- 🧪 **P3 提示词版本化与端云投机解码**：
  - **提示词版本管理 (`PromptTemplateManager`)**：Analyst / Outer 系统提示词模版版本控制与变量动态插值。
  - **Golden Dataset 离线回归评测 (`OfflineEvalRunner`)**：自动化 Golden Question 题库 Jaccard 词重叠相似度、准确率、延迟与 Token 消耗回归报告。
  - **端云混合投机解码 (`HybridSpeculativeOrchestrator`)**：本地 1B/3B 端侧模型极速生成 Draft 草稿，云端强模型（Verifier）二次校验修补，兼顾高智力与低开支。
- 🏎️ **0-阻塞高性能架构**：
  - **ConcurrentQueue 异步批处理落盘**：请求完成 1 微秒入列，后台 SQLite 批量事务落盘，主数据平面 0 I/O 阻塞。
  - **Monitor.TryEnter 非阻塞限流 Sweeper**：并发清理锁 0 阻断 HTTP 请求管道。
  - **MemoryCache SizeLimit 内存保护**：硬顶淘汰防范恶意 SessionId 膨胀攻击。

## 快速开始

### 前置要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 构建

```bash
dotnet build OptiRouter.sln -c Release
```

### 配置

1. 复制 `appsettings.example.json` 为 `appsettings.json`
2. 填入真实 ApiKey，或使用环境变量覆盖：

```bash
# Windows PowerShell
$env:OptiRouter__ProxyApiKey = "your-proxy-api-key"
$env:OptiRouter__Models__0__ApiKey = "sk-..."

# Linux / macOS
export OptiRouter__ProxyApiKey="your-proxy-api-key"
export OptiRouter__Models__0__ApiKey="sk-..."
```

`ProxyApiKey` 为空时，所有 `/v1/*` 请求都会被拒绝。

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
| `RequestsPerMinute` | 每个分区（Session > IP > Auth）的固定窗口每分钟请求上限 | `60` |
| `MaxConcurrentRequestsPerPartition` | 每个分区同时进行的最大请求数，超出返回 429 | `100` |

> **分区 Key 优先级**：`X-Session-Id` 头 > 客户端 IP（`CF-Connecting-IP` > `X-Forwarded-For` 首段 > `RemoteIpAddress`）> Bearer Token（SHA256 哈希前 16 hex）。带 API Key 的请求仍按 IP 隔离，避免单 key 多用户共享配额。

### Models[]（模型端点列表）

| 字段 | 含义 | 示例 |
|------|------|------|
| `Name` | 模型标识 | `gpt-4o` |
| `BaseUrl` | 上游 API 基地址 | `https://api.openai.com/v1` |
| `ApiKey` | 鉴权密钥 | `sk-...` |
| `Tier` | 能力分档：`Strong` / `Medium` / `Cheap` | `Strong` |
| `MaxContextTokens` | 最大上下文长度 | `128000` |
| `InputPricePerMillion` | 输入价格（美元/百万 token） | `2.5` |
| `CachedInputPricePerMillion` | 缓存命中输入价格（美元/百万 token）；省略/null 时回退普通输入价格 | `1.25` |
| `CacheWriteInputPricePerMillion` | 缓存写入输入价格（美元/百万 token）；省略/null 时回退普通输入价格 | `3.0` |
| `OutputPricePerMillion` | 输出价格（美元/百万 token） | `10.0` |
| `Provider` | 可选 provider 标识（自由字符串），仅用于 Fusion 软多样性；空表示未知 | `openai` |
| `Family` | 可选模型家族标识（自由字符串），仅用于 Fusion 软多样性；空表示未知 | `gpt-4o` |
| `TimeoutSeconds` | 单次请求超时秒数 | `120` |
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
| `UsePersistentStore` | 是否持久化成本账本到 SQLite（跨重启保留） | `true` |
| `StorePath` | SQLite 账本文件路径，仅 `UsePersistentStore=true` 时生效 | `data/optirouter-budget.db` |
| `SessionEvictionHours` | 会话账户淘汰年龄（小时）；超过此时间无活动的会话自动清理，防止内存泄漏 | `24` |

### Routing（路由策略）

| 字段 | 含义 | 默认 |
|------|------|------|
| `EnableRuleClassifier` | 按请求特征推断 Tier | `true` |
| `EnableTokenEstimator` | 估算 token 并过滤上下文不足的模型 | `true` |
| `EnableBudgetGuard` | 预算耗尽时执行降级/拒绝 | `true` |
| `EnableFailover` | 候选链顺序尝试，主模型失败自动切下一个 | `true` |
| `EnablePiiAnonymization` | 是否启用 PII 敏感数据脱敏与反向还原（手机/邮箱/身份证/卡号/IP） | `false` |
| `EnableDataSovereignty` | 是否启用数据不出域隔离屏障（强制仅路由至本地/私有节点） | `false` |
| `EnableJsonAstAutoRepair` | 是否启用 JSON AST 自动化修补服务（剥离代码围栏、修复逗号、截断补全） | `true` |
| `EnableDistributedTracing` | 是否启用 W3C 分布式链路追踪（生成 TraceId/SpanId，映射 ActivitySource） | `true` |
| `EnablePersonaDriftProtection` | 是否启用多轮对话人设一致性防护（静态人设锚点提示词） | `true` |
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
| `SemanticRouterMode` | `Hybrid`（TF-IDF 高置信短路 + 第二阶段）/ `TfIdf` / `Dense`；内置 Dense 是稳定词法特征哈希，不是训练 embedding | `Hybrid` |
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
| `EnableFusionRouter` | **融合路由**（OpenRouter Fusion 式）：非流式/流式首轮并行 panel → analyst 结构化分析 → outer 写最终答案。质量技术，成本 N+2 调用，生产默认关 | `false` |
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
| `EnableMetrics` | 启用 Prometheus `/metrics` 端点（无鉴权，仅聚合数+模型名） | `true` |
| `MetricsEndpointPath` | 指标端点路径 | `/metrics` |

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

## 测试

### 单元测试与集成测试

运行全量 503 项单元与集成测试套件：

```bash
dotnet test OptiRouter.sln -c Release
```

### 端到端冒烟测试

使用 WireMock.Net 起真实 HTTP mock server，验证完整链路（HTTP 入 → 路由 → 真实 HTTP 出 → WireMock 响应 → 回传）。

```bash
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~EndToEndSmokeTests"
```

## 离线审计分析

OptiRouter 把每条请求的成败、成本、延迟、命中分档、级联事件写进 SQLite 审计库（默认 `data/optirouter-budget.db` 的 `request_audit` 表）。`scripts/analyze_audit.py` 消费这些数据，产出 Markdown 报告，用于闭环路由策略调优的实证依据——验证规则分类误判率、各档实际成功率/成本分布、级联触发率。

零外部依赖，仅 Python 标准库 `sqlite3`。只读 DB，不改数据。

```bash
# 默认读 data/optirouter-budget.db，报告打到 stdout
python scripts/analyze_audit.py

# 指定 DB 与时间范围，写文件
python scripts/analyze_audit.py --db /path/to/optirouter-budget.db \
    --from 2026-07-01 --to 2026-08-07 --out report.md
```

## 部署

### Docker（推荐容器化部署）

多阶段构建（`sdk:8.0` 编译 → `aspnet:8.0` 运行），非 root 用户运行，内建 `HEALTHCHECK` 与 `/app/data` 数据卷。

```bash
# 构建镜像
docker build -t optirouter .

# 运行（SQLite 账本持久化到宿主目录，便于跨容器重建保留）
docker run -d --name optirouter \
  -p 5000:5000 \
  -v optirouter-data:/app/data \
  -e OptiRouter__ProxyApiKey="your-proxy-api-key" \
  -e OptiRouter__AdminApiKey="your-admin-api-key" \
  -e OptiRouter__Models__0__ApiKey="sk-..." \
  optirouter
```

## 许可证

[MIT License](LICENSE)
