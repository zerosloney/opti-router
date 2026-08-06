# OptiRouter

多模型智能路由 HTTP 代理（.NET 10）。OpenAI 兼容接口，自动选模型，省 token 降成本。

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

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

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
| `ProxyApiKey` | 调用 `/v1/*` 时使用的 Bearer API Key；为空时拒绝访问 | 空 |
| `RequestsPerMinute` | 每个来源 IP 的固定窗口每分钟请求上限 | `60` |

### Models[]（模型端点列表）

| 字段 | 含义 | 示例 |
|------|------|------|
| `Name` | 模型标识 | `gpt-4o` |
| `BaseUrl` | 上游 API 基地址 | `https://api.openai.com/v1` |
| `ApiKey` | 鉴权密钥 | `sk-...` |
| `Tier` | 能力分档：`Strong` / `Medium` / `Cheap` | `Strong` |
| `MaxContextTokens` | 最大上下文长度 | `128000` |
| `InputPricePerMillion` | 输入价格（美元/百万 token） | `2.5` |
| `OutputPricePerMillion` | 输出价格（美元/百万 token） | `10.0` |
| `TimeoutSeconds` | 单次请求超时秒数 | `120` |
| `MaxRetries` | 失败后最大重试次数 | `0` |
| `Enabled` | 是否启用该模型 | `true` |
| `Tags` | 能力标签 | `["vision", "tool-use"]` |

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
| `EnableTokenEstimator` | 粗估 token 并过滤上下文不足的模型 | `true` |
| `EnableBudgetGuard` | 预算耗尽时执行降级/拒绝 | `true` |
| `EnableFailover` | 候选链顺序尝试，主模型失败自动切下一个 | `true` |
| `LongInputThresholdTokens` | 超长输入阈值，超过则过滤短上下文模型 | `32000` |
| `DefaultTier` | 规则分类未命中时的默认分档 | `Medium` |
| `FailoverFailureThreshold` | 触发跨请求熔断的连续失败次数 | `3` |
| `FailoverCooldownSeconds` | 熔断冷却秒数，到期自动恢复 | `60` |

## 路由策略说明

1. **规则分级**（`RuleClassifierPolicy`）：按请求特征推断 Tier——代码请求→Strong，单条短问答→Cheap，复杂指令→Strong，其余→`DefaultTier`。
2. **Token 估算**（`TokenEstimator` + `LongInputPolicy`）：按 rune 分桶加权估算——CJK 按 1.5 字符/token、ASCII 按 4 字符/token、其他按 2.5，每条消息另计固定开销。超 `LongInputThresholdTokens` 时过滤掉上下文不够的模型。
3. **成本预算**（`BudgetGuardPolicy`）：日/会话预算耗尽时，`Degrade` 模式降级到 Cheap tier，`Reject` 模式返回 429。
4. **失败降级**（`FailoverPolicy` + `ProxyOrchestrator` + `ModelHealthTracker`）：候选链顺序尝试，主模型失败自动切下一个；连续失败达阈值的模型被跨请求熔断冷却，冷却到期自动恢复。

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

## 部署

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

## 已知限制

- Token 估算为分桶加权粗估（CJK/ASCII/其他按经验系数），非真实 BPE；误差约 ±15%，对路由分级足够。如需精确可接入 tiktoken 系 tokenizer。
- 跨请求熔断为简单冷却（无 HalfOpen 探测）：连续失败达阈值→冷却→到期直接放行，若仍失败会重新累计。复杂场景可升级为完整断路器。
- **Models 端点配置（BaseUrl/ApiKey/Timeout/Tier）按模型名缓存在 `ModelClientProvider`，变更需重启进程生效**。`Routing` 开关经 `IOptionsMonitor` reload 后可在线生效，与此缓存解耦。如需端点配置热更新，需改造 provider 支持 `OnChange` 触发重建 `HttpClient`。
- **成本账本持久化**：`Budget.UsePersistentStore=true`（默认）时落 SQLite 文件（`Budget.StorePath`），跨进程重启保留日/会话花费，使预算真正生效。设为 `false` 用内存实现（重启归零，仅适合测试）。
