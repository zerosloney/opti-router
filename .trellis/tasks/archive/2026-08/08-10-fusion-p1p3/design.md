# 融合路由 P1-P3 技术设计

## 1. 目标与边界

实现三个向后兼容的融合路由改进（P1 panel 温度多样性 / P2 analyst 解析加固 / P3 成本门控）。不改生产默认行为（新项默认关/沿用旧值），尊重 `routing.md` 契约。只动与三 P 相关的文件。

## 2. P1：可配置 panel 温度多样性

### 2.1 配置
`RoutingOptions` 新增：
```csharp
/// <summary>panel 采样温度。null（默认）= 沿用 FusionRouterTemperature，向后兼容。</summary>
public double? FusionRouterPanelTemperature { get; set; }
```
`FusionRouterTemperature` 保留为 analyst 温度（低温度保 JSON 稳定）。

### 2.2 行为
`FusionRouter.cs` panel request 构造（当前 `FusionRouter.cs:83`）：
```csharp
// 当前
ChatRequest panelRequest = request with { Temperature = request.Temperature ?? routing.FusionRouterTemperature };
// 改为
ChatRequest panelRequest = request with { Temperature = request.Temperature ?? routing.FusionRouterPanelTemperature ?? routing.FusionRouterTemperature };
```
`FusionRouter.cs` analyst 调用用 `routing.FusionRouterTemperature`（不变）。

### 2.3 校验（AC5）
`RouterOptionsValidator`：`FusionRouterPanelTemperature` 非 null 时须在 `[0,2]`；`FusionRouterTemperature` 既有校验保留。

### 2.4 验收（AC1）
- panel 温度 = `request.Temperature ?? PanelTemperature ?? FusionRouterTemperature`。
- analyst 温度 = `request.Temperature ?? FusionRouterTemperature`（不受 panel 温度影响）。
- `PanelTemperature=null` → panel 沿用 `FusionRouterTemperature`（向后兼容）。

## 3. P2：analyst 解析加固 + 结构化输出

### 3.1 两级容错
在 `FusionRouter.cs` analyst 调用处（当前 `:243-300`）改造：

1. **首次**：现有 `BuildAnalystRequest` + `CompleteRawAsync` + `ParseAnalysis`。
2. **解析失败时重试**：若 `ParseAnalysis` 返回 null（JSON 坏/围栏剥离后仍不可解析），构造带 `response_format={type:"json_object"}` 的 analyst 请求重试一次。`response_format` 经 `ChatRequest.ExtensionData` 注入（`ChatTypes.cs` 已支持透传未知字段）。
3. **重试仍失败 → 软降级**：用 analyst 原始文本（`ResponseConfidenceChecker.ExtractAssistantText`）构造 `FusionAnalysis { Recommendation = rawText }`，**不**回退串行，保留已付 panel 成本。

### 3.2 失败边界
- **上游失败**（异常，非解析失败）：保持现状回退串行（AC3）。
- **重试也上游失败**：回退串行。
- **软降级的分析**：只有 `Recommendation` 有值（原始文本），其余字段空。outer 仍能读 `Recommendation` 写答案。

### 3.3 实现位置
- `FusionSynthesis` 新增 `BuildAnalystRequest` 重载或 helper：构造带 `response_format` 的 analyst 请求（复用现有 `BuildAnalystInstruction`）。
- `FusionSynthesis` 新增 `BuildFallbackAnalysis(string rawText)` 返回 `FusionAnalysis { Recommendation = rawText }`。
- `FusionRouter` 在 `analysis is null` 分支插入重试 + 软降级逻辑，替换当前直接回退串行。

### 3.4 审计
- 重试请求记一条 `fusion_role="analyst"` 审计（reason 标注 `analyst retry(parse)`）。
- 软降级记日志 `analyst parse failed, degraded to raw text`。

### 3.5 验收（AC2/AC3）
- 损坏 JSON：首次解析失败 → 重试（带 response_format）→ 重试成功用重试结果。
- 重试仍坏：软降级用原始文本作 Recommendation，不回退串行。
- 上游 5xx/网络错误：回退串行（行为不变）。

## 4. P3：融合成本质量门控

### 4.1 配置
`RoutingOptions` 新增：
```csharp
/// <summary>融合路由最低复杂度门控。默认 Unknown（0，无门控）：所有请求触发，等同旧行为（向后兼容）。
/// 设 Standard 可让 Simple/Unknown 请求跳过融合。</summary>
public RequestComplexity FusionRouterMinComplexity { get; set; } = RequestComplexity.Unknown;
```

### 4.2 行为
`ProxyOrchestrator.cs:124` 融合触发条件追加：
```csharp
&& decision.RequestComplexity >= routing.FusionRouterMinComplexity
```
`Unknown`（0）< `Simple`（1）< `Standard`（2）< `Complex`（3），`>=` 比较天然满足：默认 `Unknown` 门控下所有复杂度（含 Unknown）都满足 → 等同旧行为（向后兼容）。

### 4.3 交互
- 与 `EnableDynamicFusionPanelSize` 正交：前者 gate 是否融合，后者定 panel 数。两者都读 `RequestComplexity`，不冲突。
- **门控开启**：`FusionRouterMinComplexity = Standard`（如用户显式设置）时，`Simple`/`Unknown` 请求跳过融合，`Standard`/`Complex` 触发。
- **向后兼容红线**：RuleClassifier 关闭时复杂度为 `Unknown`，默认门控（Unknown）必须放行 Unknown——否则现有融合测试与生产部署（RuleClassifier 关）会静默禁用融合。

### 4.4 校验（AC5）
`RouterOptionsValidator`：`FusionRouterMinComplexity` 为合法枚举（`Enum.IsDefined`），范围 `Unknown..Complex`。

### 4.5 验收（AC4）
- 默认 `Unknown`：所有请求触发（等同旧行为）。
- `MinComplexity=Standard`：`Simple`/`Unknown` 不触发；`Standard`/`Complex` 触发。

## 5. 兼容与回滚

- 新配置项默认值 = 旧行为（`PanelTemperature=null` 沿用、`MinComplexity=Standard` 使 Simple 跳过、P2 仅在解析失败时走新路径）。现有 `FusionRouterTests` 应全绿（AC6）。
- 回滚：删新配置项/改默认值即可，无数据迁移。
- 文档：`appsettings.example.json` + `README.md` 增补新项（AC7）。

## 6. 影响文件清单

| 文件 | 改动 |
|------|------|
| `src/OptiRouter/Configuration/RoutingOptions.cs` | +`FusionRouterPanelTemperature`、+`FusionRouterMinComplexity` |
| `src/OptiRouter/Configuration/RouterOptionsValidator.cs` | +两项校验 |
| `src/OptiRouter/Endpoints/FusionRouter.cs` | panel 温度用 PanelTemperature；analyst 重试+软降级 |
| `src/OptiRouter/Routing/FusionSynthesis.cs` | +`BuildAnalystRequest`（response_format 重载）、+`BuildFallbackAnalysis` |
| `src/OptiRouter/Endpoints/ProxyOrchestrator.cs` | 融合触发 +复杂度门控 |
| `appsettings.example.json` / `README.md` | +新配置文档 |
| `tests/.../FusionRouterTests.cs` | +P1/P2/P3 用例 |