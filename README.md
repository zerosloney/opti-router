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
- 🧪 **P3 离线评测与回归**：
  - **Golden Dataset 离线回归评测 (`OfflineEvalRunner`)**：Golden Question 题库回归报告——质量分（judge → 向量余弦 → Jaccard 兜底三级口径）、准确率、延迟与 Token 消耗，评测批次持久化并支持 A/B 对比。
  - 规划中的能力（提示词版本管理、端云 token 级投机解码、管理 SSO 等）见 [ROADMAP.md](ROADMAP.md)。
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

`appsettings.json` 中 `OptiRouter` 节点。**90% 场景只需下面几个字段**——约 80 个配置项的完整参考已移至 [docs/CONFIGURATION.md](docs/CONFIGURATION.md)，字段语义以 `RoutingOptions.cs` 代码注释为准。

### 核心配置（90% 场景只需这一节）

| 字段 | 含义 |
|------|------|
| `ConfigDbConnectionString` | 配置库连接串（未配置回退 SQLite）——路由/预算/模型/租户 Key/审计/学习状态的唯一权威存储；配一处即全量切换（多实例见 Budget StoreProvider） |
| `Models` | 上游模型端点列表（字段见 docs/CONFIGURATION.md「Models[]」）；`ApiKey` 支持 `env:VAR_NAME` 语法从环境变量加载 |
| `Routing:Preset` | 预设名 `cost-first` / `balanced` / `quality-first`（见下文「推荐配置预设」）：仅填充未显式配置的项 |
| `Budget:DailyBudgetUsd` | 日预算（美元），耗尽按 `EnforceOnExhausted` 降级/拒绝 |
| `Routing:EnableFailover` | 候选链顺序重试（默认 `true`，一般无需调整） |
| `Routing:EnableFusionRouter` | 融合路由质量模式（默认 `false`，开启前读预设说明并承担 N+2 成本） |
| `Budget:StoreProvider` | 多实例部署时改 `Postgres`/`Redis`/`MariaDb` 共享账本（默认 `Auto`） |
| `Routing:MetricsApiKey` | `/metrics` 端点 Bearer 鉴权（公网部署建议配置） |

### 安全相关默认值（公网部署前必读）

| 字段 | 含义 | 默认 |
|------|------|------|
| `AuditStoreRequestContent` | 审计库与 Dashboard 是否留存请求内容明文（opt-in；升级注意：默认值由早前版本的 `true` 改为 `false`，依赖留存的部署需显式开启） | `false` |
| `EnablePiiAnonymization` | PII 脱敏/还原（隐私敏感部署建议启用） | `false` |
| `EnableDataSovereignty` | 数据不出域屏障（合规部署建议启用） | `false` |
| `MetricsApiKey` | `/metrics` 端点鉴权（公网裸露聚合指标仍是侦察面） | `null` |


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
| 管理台角色（最小 RBAC） | 三角色 `admin`/`operator`/`viewer`：viewer 只读全部管理查询（上游密钥明文查看与管理身份列表除外，仅 admin），operator 另可执行沙箱/评测/配置写/学习重置/熔断覆写，admin 独占租户 Key、模型配置与管理身份管理。主密钥恒为 admin；附加身份经 `POST /api/dashboard/identities` 签发（明文仅返回一次，库内只存 SHA256 哈希），撤销立即失效。详见 [docs/CONFIGURATION.md](docs/CONFIGURATION.md)「管理台角色」 |

## curl 示例

非流式：

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer your-client-key" \
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
  -H "Authorization: Bearer your-client-key" \
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
  -H "x-api-key: your-client-key" \
  -H "anthropic-version: 2023-06-01" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "max_tokens": 1024,
    "messages": [{"role": "user", "content": "解释什么是多态"}]
  }'

# Gemini generateContent（鉴权：Authorization: Bearer、x-goog-api-key 或 ?key=）
curl -X POST "http://localhost:5000/v1beta/models/auto:generateContent" \
  -H "x-goog-api-key: your-client-key" \
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

**策略横评**：`scripts/strategy-bakeoff.ps1` 用审计时间窗总账 + 评测跑批，横向对比"cheap-only / 全池 / 融合"等策略组合的成本与质量——方法论、口径与报告模板见 [docs/strategy-bakeoff.md](docs/strategy-bakeoff.md)。

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
  -e OptiRouter__AdminApiKey="your-admin-api-key" \
  -e OptiRouter__ConfigDbConnectionString="Server=mariadb;Port=3306;Database=optirouter;User ID=optirouter;Password=..." \
  optirouter

# 或最小化运行（不配 DB 时回退 SQLite 文件，挂载 /app/data 卷持久化）
# docker run -d --name optirouter -p 5000:5000 \
#   -v optirouter-data:/app/data \
#   -e OptiRouter__AdminApiKey="your-admin-api-key" optirouter
# 启动后用 AdminApiKey 登录管理台，在 Keys 页创建租户 Client Key（持久化于
# data/client-keys.json 或配置库），/v1/* 请求以该 key 作为 Bearer 调用。
```

> 说明：全局 `ProxyApiKey` 已移除，`/v1/*` 鉴权一律使用管理台 Keys 页创建的租户 Client Key。

### 多实例 / Kubernetes

最小清单（无状态 Deployment + `/health` 探针）与多副本存储矩阵前提见 [deploy/k8s/](deploy/k8s/)：replicas > 1 必须使用服务器型存储后端（配置库 MariaDB；账本/断路器 `Postgres`/`Redis`/`MariaDb`），进程内存状态（响应缓存/健康统计/亲和）跨 Pod 不同步。

## 运维备忘

- **发版**：`powershell -ExecutionPolicy Bypass -File scripts\release.ps1`——配置预检（src vs publish 差异即中止，防旧版回写生产配置）→ 停服 → publish → 启服 → `/health` 验证；构建版本由 MinVer 从 `v*` git tag 自动派生（发版 = 打 tag）。
- **日志**：Serilog 滚动文件 `logs/service-yyyyMMdd.log`——按天分文件、单文件 50MB 上限、自动保留最近 14 个（约 700MB 封顶后淘汰最旧），无需人工清理。启动早期的控制台输出（Serilog 初始化前）重定向在 `logs/boot.log`（`start-local.cmd`）。
- **配置库备份**：配置库（MariaDB `optirouter_*` 表 / SQLite `data/optirouter-config.db`）是路由、预算、模型与租户 Key 的唯一权威，建议纳入例行备份：
  - MariaDB：`mysqldump -h127.0.0.1 -uroot -p test optirouter_app_config optirouter_client_keys > optirouter-config-backup.sql`
  - SQLite：直接复制 `data/optirouter-config.db`（服务停止时，或用 `.backup` 语义的工具）
- **安全加固**（公网部署前）：设置强随机 `AdminApiKey`；`TrustProxyHeaders=true` 仅在可信反代之后开启；`OptiRouter:Routing:MetricsApiKey` 建议配置以免 `/metrics` 裸露；管理 API 的 Bearer 失败尝试与登录页共享同一 IP 锁定窗口（5 次失败锁 5 分钟）。

## 许可证

[MIT License](LICENSE)
