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
$env:OptiRouter__Models__0__ApiKey = "sk-..."

# Linux / macOS
export OptiRouter__Models__0__ApiKey="sk-..."
```

### 运行

```bash
dotnet run --project src/OptiRouter
```

应用默认监听 `http://localhost:5000`。

### 健康检查

```bash
curl http://localhost:5000/health
```

## 配置说明

`appsettings.json` 中 `OptiRouter` 节点各字段含义：

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

## 已知限制

- Token 估算为分桶加权粗估（CJK/ASCII/其他按经验系数），非真实 BPE；误差约 ±15%，对路由分级足够。如需精确可接入 tiktoken 系 tokenizer。
- 跨请求熔断为简单冷却（无 HalfOpen 探测）：连续失败达阈值→冷却→到期直接放行，若仍失败会重新累计。复杂场景可升级为完整断路器。
