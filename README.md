# OptiRouter

多模型智能路由 HTTP 代理（.NET 8）。OpenAI 兼容接口，自动选模型，省 token 降成本。

## 架构

```
┌──────────┐   POST /v1/chat/completions   ┌──────────────┐
│  Client  │ ────────────────────────────▶ │ OptiRouter  │
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
            │ BudgetGuard │ → 预算耗尽降级/拒绝            │ FailoverPolicy│ → 排除失败模型
            └─────────────┘                               └───────────────┘
                          │             候选链 [A, B, C]
                          ▼
                  ProxyOrchestrator 顺序尝试
                          │
            ┌─────────────┼─────────────┐
            ▼             ▼             ▼
       ┌────────┐   ┌────────┐   ┌────────┐
       │ Model A│   │ Model B│   │ Model C│  (OpenAI 兼容上游)
       └────────┘   └────────┘   └────────┘
```

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
| `LongInputThresholdTokens` | 超长输入阈值，超过则过滤短上下文模型 | `32000` |
| `DefaultTier` | 规则分类未命中时的默认分档 | `Medium` |
| `TokenEstimation` | token 估算模式：`Tiktoken` 真实 BPE 精确计数 / `Bucket` 分桶粗估 | `Tiktoken` |
| `TiktokenEncoding` | Tiktoken 编码名（仅 `TokenEstimation=Tiktoken` 时生效） | `o200k_base` |
| `FailoverFailureThreshold` | 触发跨请求熔断的连续失败次数 | `3` |
| `FailoverCooldownSeconds` | 熔断冷却秒数，到期进入半开探测 | `60` |
| `FailoverHalfOpenMaxProbes` | 半开态允许的最大并发探测请求数 | `1` |
| `FailoverHalfOpenRequiredSuccesses` | 半开态连续探测成功多少次后才闭合熔断（防单次偶然成功导致抖动） | `1` |
| `EnableHealthProbe` | 是否启用后台主动健康探活（定时对所有启用模型探测，结果上报断路器） | `true` |
| `HealthProbeIntervalSeconds` | 后台探活间隔秒数 | `60` |
| `EnableSemanticRouter` | 是否启用向量空间语义路由（离线词袋模型，余弦相似度匹配） | `true` |
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
| `EnableLatencyAware` | 同 tier 段按历史延迟重排（快模型优先），后台聚合零 I/O | `false` |
| `LatencyMinSamples` | 延迟排序生效所需最小样本数，低于此值不参与排序 | `10` |
| `LatencyStatsWindowMinutes` | 延迟聚合统计窗口（分钟），窗口越长越平滑但响应慢 | `60` |
| `EnableCapabilityFilter` | 按请求能力需求（vision/tool-use/json-mode）排除 Tags 不含的模型 | `false` |
| `EnableFusionMode` | 并行首试：非流式首轮并行前 N 候选取最快成功，取消其余 | `false` |
| `FusionMaxParallel` | 并行首试首轮并发数，范围 `[2, 5]` | `2` |
| `EnableFusionRouter` | **融合路由**（OpenRouter Fusion 式）：非流式首轮并行 panel → analyst 结构化分析 → outer 写最终答案。质量技术，成本 N+2 调用，生产默认关 | `false` |
| `FusionRouterPanelSize` | 融合路由 panel 并行模型数，范围 `[2, 5]` | `3` |
| `EnableDynamicFusionPanelSize` | 按 typed request complexity 在最小/最大范围内动态选 panel 数；不解析 reason 文本 | `false` |
| `FusionRouterMinPanelSize` | 动态 Fusion panel 最小数，范围 `[2, 5]` 且不得大于 `FusionRouterPanelSize` | `2` |
| `EnableFusionDiversity` | 软优先不同 `Provider`/`Family`，元数据不足时按原候选顺序补齐 | `false` |
| `FusionRouterAnalystModel` | 融合路由 analyst 模型名（留空=主候选）；只产结构化 JSON | `null` |
| `FusionRouterAnalystPrompt` | 融合路由 analyst 专用 JSON 分析提示词（留空=内置提示词；不复用级联自校验提示词） | `null` |
| `FusionRouterOuterModel` | 融合路由 outer 模型名（留空=主候选）；读分析写最终答案 | `null` |
| `FusionRouterMaxOutputTokens` | 融合路由 outer 答案最大输出 token 数 | `16000` |
| `FusionRouterTemperature` | 融合路由 panel/analyst 采样温度，范围 `[0, 2]`；原请求显式温度优先 | `0.0` |
| `FusionRouterPanelTemperature` | panel 专用采样温度；`null`=沿用 `FusionRouterTemperature`。panel 发散建议 `>0`，analyst/outer 仍用 `FusionRouterTemperature` 保 JSON 稳定 | `null` |
| `FusionRouterMinComplexity` | 融合路由最低复杂度门控（`Unknown`/`Simple`/`Standard`/`Complex`）；低于此值的请求不触发融合，省 ×N 成本。`Unknown`（默认）关闭门控=全量融合（向后兼容）；设 `Standard` 可让 Simple 请求跳过融合 | `Unknown` |
| `EnableMetrics` | 启用 Prometheus `/metrics` 端点（无鉴权，仅聚合数+模型名） | `true` |
| `MetricsEndpointPath` | 指标端点路径 | `/metrics` |

## 路由策略说明

1. **规则分级**（`RuleClassifierPolicy`）：按请求特征推断 Tier——代码请求按意图细分（复杂代码 debug/fix/重构/算法→Strong，简单代码 hello world/脚手架/示例→Medium，无明确意图或解释类→Strong 保守处理；意图检测只跑在指令文本上，剔除代码块避免正文误判），数学/公式→Strong，翻译→Medium，单条短问答→Cheap，复杂指令→Strong，其余→`DefaultTier`。
2. **语义路由**（`SemanticRouterPolicy`）：向量空间词袋模型（VSM），100% 离线、零依赖。对最近一条 user 消息做 token 化与 L2 归一化，与各 `SemanticRoutes[].Phrases` 的归一化向量算余弦相似度（归一化后退化为点积），最高相似度 ≥ `SemanticSimilarityThreshold` 则覆盖候选为目标 `TargetTier`。适合「代码助手→Strong」「闲聊→Cheap」等意图分流，配置变更热生效。
3. **Token 估算**（`ITokenEstimator` + `LongInputPolicy`）：默认 `Tiktoken` 模式，用 SharpToken（tiktoken 的 C# 移植，词表内嵌、离线可用）按真实 BPE 精确计数，每条消息另计 3 token 开销，编码由 `TiktokenEncoding` 指定（默认 `o200k_base`）；计数异常时自动回退到分桶粗估。`Bucket` 模式按 rune 分桶加权估算——CJK 按 1.5 字符/token、ASCII 按 4 字符/token、其他按 2.5。超 `LongInputThresholdTokens` 时过滤掉上下文不够的模型。
4. **成本预算**（`BudgetGuardPolicy`）：日/会话预算耗尽时，`Degrade` 模式降级到 Cheap tier，`Reject` 模式返回 429。
5. **失败降级**（`FailoverPolicy` + `ProxyOrchestrator` + `ModelHealthTracker`）：候选链顺序尝试，主模型失败自动切下一个；连续失败达阈值的模型触发三态断路器——Closed（正常）→ Open（熔断冷却）→ HalfOpen（冷却到期，最多放行 `FailoverHalfOpenMaxProbes` 个并发探测）；探测成功则闭合恢复，探测失败则重新进入冷却。流式请求的中途失败同样计入熔断。
6. **能力过滤**（`CapabilityFilterPolicy`，策略链首位，默认关）：按请求内容检测所需能力——`image_url` 多模态内容→需 `vision`，`tools` 非空数组→需 `tool-use`，`response_format.type=json_object`→需 `json-mode`；排除 `Tags` 不含所需能力的模型。无能力需求时透传，过滤后为空时保留原候选（让上游报错，避免误配 Tags 导致全拒）。能力标注通过 `Models[].Tags` 表达。
7. **规则分级扩展**（`RuleClassifierPolicy`）：在原有代码/简单问答检测上新增——数学/公式（LaTeX 环境、`\frac`、求解/求导/积分）→Strong，翻译请求（`translate...to`、`翻译...为`、`把...翻译成`）→Medium。低误报正则模式，代码优先级最高。
8. **延迟感知**（`LatencyAwarePolicy`，默认关）：后台 `LatencyStatsAggregatorService` 周期聚合审计表成功请求延迟（窗口 `LatencyStatsWindowMinutes`，默认 60 分钟），写入内存快照。策略同 tier 段内按 `1/(avgMs + 0.5×p95ms + 50ms)` 排序（快模型优先，p95 项压制「avg 稳但 tail 差」的模型），样本数不足 `LatencyMinSamples` 的尾部保留，冷启动透传。决策层零 I/O、零锁。
9. **并行首试**（`EnableFusionMode`，默认关，仅非流式）：首轮并行尝试候选链前 `FusionMaxParallel` 个模型，取最快成功响应，取消其余。成本语义见下方「已知限制」。流式不支持（首 chunk 锁定模型无法切换）。全失败/取消回退串行降级链。
10. **级联自校验**（`EnableCascadeUpgrade`，默认关，仅非流式）：路由到 Cheap 的请求按 `CascadeUpgradeSampleRate` 采样，用同 Cheap 模型判定 CONFIDENT/UNCERTAIN，低置信则升级首个 Strong 模型重答。复核调用的 token 成本计入账本。
11. **融合路由**（`EnableFusionRouter`，默认关，仅非流式）：参照 OpenRouter Fusion Router / Mixture-of-Agents——首轮并行叫候选链前 `FusionRouterPanelSize` 个模型（panel）独立作答并**全部收集**（非取最快），`analyst` 模型（默认主候选）读全部回答产出结构化 JSON（共识/矛盾/覆盖缺口/独特洞察），`outer` 模型（默认主候选）再依分析撰写最终答案。成本 ≈ N panel + 1 analyst + 1 outer 次调用，是质量技术。panel 全程按真实/预估成本入账（`ParallelGroupId` 共享，审计 `FusionRole` 区分 panel/analyst/outer）。panel 全失败或 analyst 解析失败自动降级；与 `EnableFusionMode`（并行 race）同开时，质量 Fusion Router 优先，失败后再尝试 race，否则回退串行链。
12. **上游配额感知**（`QuotaAwarePolicy`，默认关）：客户端只规范化已知 rate-limit/reset/retry headers，未知 header 忽略且不保存原始值。策略只读进程内不可变快照、无网络/磁盘 I/O；429 触发本次请求 failover 和已知 reset 冷却，但不计断路器失败或 Thompson 坏反馈。网络、超时与 5xx 保持原健康失败语义。
13. **稳定前缀缓存粘性**（`PromptCacheAffinityPolicy`，默认关）：按有序 system messages 与稳定的 `tools`/`functions`/`tool_choice`/`response_format` 字段计算 SHA-256，仅缓存哈希与模型名。显式 Session affinity 优先；预算、配额、上下文与健康约束仍可覆盖缓存偏好。

缓存成本按 `cache hit`、`cache write`、剩余 uncached prompt token 分别计价。审计记录保存三类 token、总延迟与 TTFT。流式 TTFT 是首个上游 SSE `data:` 项的实际时间；非流式无法看到首 token，故保存的是“上游响应头可用延迟代理”，不要把它解读为字面首 token 延迟。

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

流式：

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

### 单元测试

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

报告维度：

- **Summary**：总请求、整体成功率、总成本、平均/p95 延迟、最贵/最慢模型。
- **By Model**：每个模型的请求数、成功率、p95 延迟、总成本、单位成本（$/1k 请求、$/1M token）。
- **By Routed Tier**：按路由命中档聚合。**Cheap 档低成功率**或 **Strong 档处理极短 prompt** 是规则分类误判的离线信号。
- **Cascade Upgrade**：级联自校验触发率、升级率、升级源模型分布。
- **By Routing Reason Signal**：按路由原因关键词（`code-detected` / `simple-qa` / `semantic-router: matched` 等）分组。某信号的成功率/成本与同类异常（如 `code-detected` 命中却是自然语言短文本）提示需人工复核 reason 文本。
- **Daily Trend**：按天聚合请求量、成功率、成本。

报告只给统计与信号提示，规则误判的精确判定仍需人工结合 reason 文本复核。

### 合成数据生成器（无真实流量时验证闭环）

`scripts/generate_audit_data.py` 生成符合 `request_audit` schema 的合成数据，用于在没有真实生产流量时跑通「落盘 → analyze_audit → 人工看信号 → 调参」闭环，验证分析报告与路由假设。零外部依赖，默认写独立库（不碰真实 `optirouter-budget.db`），同 `--seed` 完全可复现。

```bash
# 生成 1000 行合成数据到独立演示库
python scripts/generate_audit_data.py --rows 1000 --seed 42

# 分析生成的演示库
python scripts/analyze_audit.py --db data/audit-demo.db

# 注入 50 条「本该 Strong 却被路由到 Cheap」的受控误判，验证报告能暴露误判信号
python scripts/generate_audit_data.py --rows 500 --seed 42 --misclassify 50 --db data/audit-mc.db

# 模拟级联自校验（Cheap 档 50% 触发）与并行竞速（30%），验证 Cascade/并行审计字段
python scripts/generate_audit_data.py --rows 300 --seed 5 --cascade-rate 0.5 --parallel-rate 0.3 --db data/audit-cp.db
```

参数：`--rows`（行数）、`--seed`（可复现种子）、`--db`（默认 `data/audit-demo.db`）、`--append`（显式追加既有库）、`--misclassify`（注入误判条数）、`--cascade-rate`（级联触发比例）、`--parallel-rate`（并行竞速比例）、`--models-json`（可选模型画像覆盖）。

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

配置通过环境变量注入（双下划线 `__` 分隔层级，对应 `OptiRouter:*` 配置节）。容器内监听 HTTP `5000`，TLS 由外部反代终结（见下方 HTTPS 要求）。

验证：

```bash
curl http://localhost:5000/health    # → Healthy
curl http://localhost:5000/metrics   # → Prometheus 指标（无需 API Key）
```

> 镜像默认环境：`ASPNETCORE_URLS=http://+:5000`、`ASPNETCORE_ENVIRONMENT=Production`、`OptiRouter__Budget__StorePath=data/optirouter-budget.db`。

### HTTPS 要求

生产环境**必须**使用 HTTPS 终结 API Key 传输。两种方式：

1. **Kestrel 直接 TLS 终结**（推荐单实例）：
   ```bash
   export ASPNETCORE_URLS="https://+:443;http://+:80"
   ```
   需配置 Kestrel 证书（可通过 `appsettings.json` 或环境变量）。

2. **反向代理 TLS 终结**（推荐多实例）：
   - 在 Nginx / Caddy / Azure App Service 层终结 HTTPS
   - 设置 `X-Forwarded-Proto` 头
   - 应用监听 `http://localhost:5000`

启动时若 `ASPNETCORE_URLS` 不含 `https://`，会在日志中输出警告。

### 成本账本数据

默认 `data/optirouter-budget.db`（SQLite）。目录不存在自动创建。该文件累加所有请求的成本数据，建议纳入备份/监控范围。

### 指标监控（Prometheus）

`EnableMetrics=true`（默认）时暴露 `/metrics` 端点（prometheus-net，标准 Prometheus exposition format）。该端点**无需 API Key**（同 `/health`，便于抓取），仅暴露聚合数与模型名，不含 API Key 或 PII。

**暴露的指标**：

| 指标 | 类型 | 标签 | 含义 |
|------|------|------|------|
| `optirouter_proxy_requests_total` | counter | model, tier, outcome, streaming | 模型尝试数（每次候选尝试计一次）；outcome: success/timeout/stream_error/model_error/error |
| `optirouter_tokens_total` | counter | model, direction | token 消耗（direction: input/output） |
| `optirouter_cost_usd_total` | counter | model | 累计美元成本 |
| `optirouter_request_duration_ms` | histogram | model, streaming | 单次尝试延迟（50ms~200s 指数桶） |
| `optirouter_time_to_first_token_ms` | histogram | model, streaming | 流式首 data 项 TTFT；非流式为响应头延迟代理 |
| `optirouter_cache_tokens_total` | counter | model, kind | 缓存 token（kind: hit/write/uncached） |
| `optirouter_quota_limited_total` | counter | model | 上游 429 配额拒绝次数 |
| `optirouter_circuit_failure_count` | gauge | model | 断路器当前连续失败数 |
| `optirouter_daily_spend_usd` | gauge | — | 当日 UTC 花费 |
| `optirouter_total_spend_usd` | gauge | — | 进程生命周期累计花费 |
| `http_requests_received_total` | counter | code, method, endpoint | ASP.NET 请求计数（prometheus-net 内建） |
| `http_requests_in_progress` | gauge | — | 当前在途请求数 |

> Gauge 型指标（花费/断路器）由后台 `MetricsGaugeUpdaterService` 复用 `HealthProbeIntervalSeconds` 周期刷新；Counter/Histogram 在请求路径即时记录。

**Prometheus scrape 配置示例**：

```yaml
scrape_configs:
  - job_name: optirouter
    scrape_interval: 15s
    static_configs:
      - targets: ["your-host:5000"]
```

关闭指标导出：设 `OptiRouter:Routing:EnableMetrics=false`。自定义端点路径：`OptiRouter:Routing:MetricsEndpointPath=/custom-metrics`。

### CI（GitHub Actions）

`.github/workflows/ci.yml` 在 push 到 `master` 或 PR 时触发：

1. **build-test**（每次）：`dotnet restore` + `dotnet build -c Release -warnaserror`（强制零警告）+ `dotnet test`（带 XPlat 覆盖率，产物上传 artifact）。
2. **docker-build**（仅 push 到 master）：构建 Docker 镜像验证 Dockerfile 可用（不打 tag 推送）。

同分支并发自动取消旧运行，节省 CI 配额。

## 已知限制

- Token 估算默认使用 SharpToken 真实 BPE 精确计数（`TokenEstimation=Tiktoken`），计数异常时回退到分桶粗估；仅当显式配置 `TokenEstimation=Bucket` 或回退触发时才有分桶误差（约 ±15%）。注意 BPE 计数基于配置的编码（默认 `o200k_base`），与上游模型实际分词器可能存在少量偏差。
- 跨请求熔断为三态断路器（Closed / Open / HalfOpen），半开态按 `FailoverHalfOpenMaxProbes` 限量并发探测；按 `FailoverHalfOpenRequiredSuccesses` 要求连续探测成功多次才闭合（默认 1，调高可防单次偶然成功导致抖动）。尚不支持指数退避冷却。
- **后台主动探活**：`EnableHealthProbe=true`（默认）时，启动预热一轮后按 `HealthProbeIntervalSeconds`（默认 60s）周期对所有启用模型探测，结果上报断路器（成功累计半开/闭合，失败计熔断）。探活串行执行，单次失败仅记录不中断服务。
- **Models 端点配置支持热更新**：连接相关字段（`BaseUrl`/`ApiKey`/`TimeoutSeconds`）变化时，`ModelClientProvider` 经 `IOptionsMonitor.OnChange` 自动重建对应模型的客户端；旧客户端保留宽限期（默认 2 分钟）后释放，不打断在途请求。`Tier`/价格/上下文长度等路由字段每请求读取，变化下次路由即生效。reload 由配置源的变更通知触发（`appsettings.json` 默认 `reloadOnChange` 开启；环境变量源不支持 reload，仍需重启）。
- **限流阈值分区定型**：`RequestsPerMinute` 每请求读取，但 `FixedWindowRateLimiter` 的 `PermitLimit` 在分区首次创建时定型——运行时改配置仅对新建分区生效，既有分区沿用创建时的值。变更全局生效需重启进程。`MaxConcurrentRequestsPerPartition` 同理。
- **成本账本持久化**：`Budget.UsePersistentStore=true`（默认）时落 SQLite 文件（`Budget.StorePath`），跨进程重启保留日/会话花费，使预算真正生效。设为 `false` 用内存实现（重启归零，仅适合测试）。
- **并行首试成本语义**：`EnableFusionMode=true` 时，被取消/失败的并行尝试拿不到上游真实 Usage（响应未完整返回），但上游对已发出的请求仍计费。OptiRouter 按 `EstimatedInputTokens × 模型 input 价格` 记一笔预估成本到账本，审计记录标注 `IsEstimated=true` 以区分真实成本。预估为下限（仅 input，未含已生成的部分 output），实际偏差随 `FusionMaxParallel` 增大；采纳的成功响应记真实成本（`IsEstimated=false`）。
- **融合路由成本语义**：`EnableFusionRouter=true` 时，panel 调用全部按真实/预估成本入账（同并行首试），analyst 与 outer 调用记真实成本。总成本 ≈ N panel + 1 analyst + 1 outer，随 `FusionRouterPanelSize` 线性增长。panel 全失败或 analyst 解析失败自动回退串行，不浪费已成功的 panel 调用的成本。仅非流式（流式首 chunk 锁定模型无法切换 panel）。
- **配额状态仅进程内**：规范化的请求/token 余量与 reset 窗口不会写 SQLite，也不会跨 OptiRouter 副本协调；进程重启后回到未知余量，多个副本各自学习上游配额。原始 rate-limit headers 不存储、不记录日志。
- **延迟感知冷启动**：`EnableLatencyAware=true` 时，新模型或低流量模型样本数不足 `LatencyMinSamples`，不参与延迟排序，退回 `MaxContextTokens` 排序；聚合服务复用 `HealthProbeIntervalSeconds` 周期，首次请求前预热一轮。
- **流式中途失败契约**：流式响应（SSE）一旦首 chunk 已透传，HTTP 状态码（200）与 header 无法回退，代理不能像非流式那样 failover 切换模型。故流式中途失败（上游断连、超时、超出 `MaxResponseStreamBytes` 上限）的信号**内嵌于 SSE 流**而非 HTTP 层：代理在已透传的 chunk 之后注入一个 OpenAI 兼容的 error event，再以 `data: [DONE]` 干净终止，连接正常关闭而非硬断。客户端 SDK 必须解析这种内嵌错误。error event 形如：
  ```
  data: {"error":{"message":"upstream connection reset mid-stream","type":"upstream_error","code":"UPSTREAM_ERROR"}}

  data: [DONE]
  ```
  `type` / `code` 字段供机读，按错误来源区分：
  | code | type | 含义 | 客户端重试建议 |
  |------|------|------|---------------|
  | `UPSTREAM_ERROR` | `upstream_error` | 上游断连/IO 错误（首 chunk 后） | 可重试（换请求或换模型） |
  | `TIMEOUT` | `timeout` | HttpClient 内部超时（首 chunk 后） | 可重试（调高超时或换模型） |
  | `RESPONSE_TOO_LARGE` | `response_too_large` | 超出 `MaxResponseStreamBytes` / 单行字节上限 | 不可重试，排查上游输出或调高上限 |
  | `INTERNAL_ERROR` | `server_error` | 代理内部错误 | 不可重试，排查日志/配置 |
  | `BUDGET_EXHAUSTED` | `budget_exceeded` | 预算耗尽 | 等预算重置或调高预算 |
  | `ALL_CANDIDATES_FAILED` | `all_candidates_failed` | 首 chunk 前所有候选均失败 | 检查模型健康/熔断状态 |

  客户端实现要点：流式 reader 收到 `data:` 行后先尝试解析 JSON；若含 `error` 对象字段即按上表判定。收到 `[DONE]` 才视为流结束；若连接在 `[DONE]` 前断开且未收到 error event，按传输层错误重试。
