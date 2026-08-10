# 融合路由 P1-P3 改进实现

## Goal

落地研究报告（`08-10-fusion-router-algo-research`）中优先级最高的三个低成本、修根本矛盾的改进提案：

- **P1**：可配置 panel 温度多样性（修 G1：`FusionRouterTemperature=0` 锁死 panel 多样性）
- **P2**：analyst 解析加固 + 结构化输出（修 G3：`ParseAnalysis` 单次解析，损坏即白付 N+1 成本回退）
- **P3**：融合成本质量门控（修 G4：只要条件满足就全量融合，Simple 请求也付 ×N 成本）

三个改动均**默认关 / 向后兼容**，尊重 `.trellis/spec/backend/routing.md` 既有契约（panel 超时、diversity、动态 size、audit/FusionRole 语义）。本任务只实现这三个，不做 P4-P8。

## Confirmed Facts（代码勘察）

### P1 现状
- `FusionRouterTemperature`（`RoutingOptions.cs:255`）默认 `0.0`，`FusionRouter.cs:83` 用 `request.Temperature ?? routing.FusionRouterTemperature` 设 panel 温度。analyst 在 `FusionSynthesis.BuildAnalystRequest` 用同一 `temperature` 参数。
- 问题：panel 与 analyst 共用同一温度，且默认 0 → 同模型 panel 输出同质化，多样性机制失效。

### P2 现状
- `FusionSynthesis.ParseAnalysis`（`FusionSynthesis.cs`）单次 `JsonDocument.Parse`，`catch (JsonException) return null`。
- `FusionRouter.cs` analyst 调用后 `analysis is null` → 回退串行（白付 N+1 成本）。
- `ChatRequest.ExtensionData`（`ChatTypes.cs`）可透传 `response_format` 等未知字段——P2 重试可用。

### P3 现状
- `ProxyOrchestrator.cs:124-127` 融合触发条件：`EnableFusionRouter && !fusionRouterAttempted && failedInThisRequest.Count==0 && !request.Stream && decision.Candidates.Count>=2`。
- `RequestComplexity` 枚举（`RequestComplexity.cs`）：`Unknown=0 / Simple=1 / Standard=2 / Complex=3`。`RequestComplexity` 是 typed 信号（非解析 reason），`RouterDecision.RequestComplexity` 已有。
- 现有 `EnableDynamicFusionPanelSize` 已按 `RequestComplexity` 动态定 panel size（Simple→min / Standard→min+1 / Complex→max）。

### 测试基础设施
- `tests/OptiRouter.Tests/Routing/FusionRouterTests.cs`：`FusionRouterFactory`（三同 tier 模型，`ConfigureWebHost` 设 config），`TestProvider`（mock 客户端），`BuildRequest`/`MakeResponse` helper。走完整 HTTP 管道。
- `ChatRequest`/`ChatMessage` 在 `ChatTypes.cs`；`model-a/b/c` 三模型已配好。

## Requirements

### R1（P1）可配置 panel 温度多样性
- 新增 `RoutingOptions.FusionRouterPanelTemperature`（`double?`，默认 `null` = 沿用 `FusionRouterTemperature`，向后兼容）。
- panel 温度：`request.Temperature ?? routing.FusionRouterPanelTemperature ?? routing.FusionRouterTemperature`。
- analyst 温度保持用 `FusionRouterTemperature`（低温度保 JSON 稳定），**不**受 panel 温度影响。
- 语义：panel 用于发散采样，analyst/outer 用于收敛稳定。

### R2（P2）analyst 解析加固 + 结构化输出
- 新增解析容错：`ParseAnalysis` 内先尝试现有剥离+解析；失败时尝试 `response_format={type:"json_object"}` 重试一次（经 `ExtensionData` 注入，若模型支持）。
- 解析仍失败时**软降级**：用 analyst 原始文本作为 `Recommendation`（保住已付 panel 成本），**不**直接回退串行——除非 analyst 请求本身失败（上游错误）。
- 保持向后兼容：`FusionRouterAnalystPrompt` 自定义时行为不变（除非解析失败才走新路径）。

### R3（P3）融合成本质量门控
- 新增 `RoutingOptions.FusionRouterMinComplexity`（`RequestComplexity`，默认 `Unknown`=无门控，向后兼容）。
- `ProxyOrchestrator` 融合触发条件追加：`decision.RequestComplexity >= routing.FusionRouterMinComplexity`。
- 默认 `Unknown` 时所有请求触发（等同旧行为）；设 `Standard` 时 `Simple` 请求跳过融合（但 RuleClassifier 关闭时复杂度为 Unknown，也会被跳过——这是显式开启门控的预期后果）。
- 向后兼容关键：RuleClassifier 关闭时复杂度为 `Unknown`，默认门控不得跳过它（否则现有融合测试全崩）。

## Acceptance Criteria

- [ ] **AC1（P1）**：`FusionRouterPanelTemperature` 配置可单独控制 panel 温度；analyst 仍用 `FusionRouterTemperature`。单测验证：panel 温度=配置值、analyst 温度=FusionRouterTemperature；`PanelTemperature=null` 时 panel 沿用 `FusionRouterTemperature`（向后兼容）。
- [ ] **AC2（P2）**：analyst 输出损坏 JSON 时，先 `response_format` 重试；重试成功则用重试结果；重试仍失败则软降级用原始文本作 `Recommendation`（不回退串行）。单测验证：损坏 JSON 不抛异常、不白付、有 actionable 输出。
- [ ] **AC3（P2）**：analyst 请求本身上游失败（非解析失败）时仍回退串行（行为不变）。
- [ ] **AC4（P3）**：`FusionRouterMinComplexity=Unknown`（默认）时所有请求触发（等同旧行为，向后兼容）；设为 `Standard` 时 `Simple` 复杂度请求不触发融合（无 FusionRole 行），`Standard`/`Complex` 触发。单测验证触发边界。
- [ ] **AC5**：所有新配置项有校验（`RouterOptionsValidator`）：`FusionRouterPanelTemperature` 范围 `[0,2]`（可为 null）；`FusionRouterMinComplexity` 合法枚举值。
- [ ] **AC6**：向后兼容——未配置新项时行为与现状完全一致；现有 `FusionRouterTests` 全绿。
- [ ] **AC7**：`appsettings.example.json` 与 `README.md` 文档更新新配置项。

## Out of Scope

- 不做 P4-P8（panel 预筛、analyst/outer 超时、outer 一致性校验、MoA 迭代、Pareto 定位）。
- 不改 `FusionRouterPanelSize` / `EnableDynamicFusionPanelSize` / `EnableFusionDiversity` 既有语义。
- 不接真实付费上游。

## Open Questions

- `FusionRouterMinComplexity` 与 `EnableDynamicFusionPanelSize` 的交互：两者都读 `RequestComplexity`，语义正交（一个 gate 是否融合，一个定 panel 数），不冲突。无需用户裁定。