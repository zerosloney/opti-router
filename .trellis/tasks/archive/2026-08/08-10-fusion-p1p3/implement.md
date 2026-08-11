# 融合路由 P1-P3 执行计划

## 阶段 1：配置层（P1+P3 配置项 + 校验）

- [ ] 1.1 `RoutingOptions.cs`：+`FusionRouterPanelTemperature`（`double?`，默认 null）、+`FusionRouterMinComplexity`（`RequestComplexity.Standard`）。
- [ ] 1.2 `RouterOptionsValidator.cs`：+PanelTemperature `[0,2]`（非 null 时）、+MinComplexity 合法枚举校验。
- [ ] 1.3 `appsettings.example.json` + `README.md`：+两新配置文档（AC7）。

## 阶段 2：P1 + P3 行为

- [ ] 2.1 `FusionRouter.cs` panel 温度：`request.Temperature ?? PanelTemperature ?? FusionRouterTemperature`（AC1）。
- [ ] 2.2 `ProxyOrchestrator.cs` 融合触发：+`decision.RequestComplexity >= FusionRouterMinComplexity`（AC4）。

## 阶段 3：P2 analyst 加固

- [ ] 3.1 `FusionSynthesis.cs`：+`BuildAnalystRequest`（带 `response_format` 重载/helper）、+`BuildFallbackAnalysis(string rawText)`。
- [ ] 3.2 `FusionRouter.cs`：analyst 解析失败 → response_format 重试一次 → 仍失败软降级用原始文本（AC2/AC3）。

## 阶段 4：测试

- [ ] 4.1 P1 测试：panel 温度=PanelTemperature、analyst=FusionRouterTemperature、null 沿用（AC1）。
- [ ] 4.2 P2 测试：损坏 JSON 重试成功/重试仍失败软降级/上游失败回退串行（AC2/AC3）。
- [ ] 4.3 P3 测试：Simple 不触发、Standard/Complex 触发、MinComplexity=Unknown 全触发（AC4）。
- [ ] 4.4 校验测试：PanelTemperature 越界、MinComplexity 非法枚举 fail（AC5）。
- [ ] 4.5 既有 `FusionRouterTests` 全绿（AC6 向后兼容）。

## 阶段 5：验证

- [ ] 5.1 `dotnet build OptiRouter.sln -c Release` 通过。
- [ ] 5.2 `dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~FusionRouterTests"` 全绿。
- [ ] 5.3 全量测试 `dotnet test OptiRouter.sln -c Release` 通过（确认无回归）。

## 验证命令

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~FusionRouterTests"
dotnet test OptiRouter.sln -c Release
```

## 风险点 / 回滚

- P2 重试多耗一次 analyst 调用（仅解析失败时）→ 比例低，且软降级兜底。
- P3 门控可能漏掉该融合的 Simple 复杂请求 → `RequestComplexity` 是 typed 信号，风险可控；`MinComplexity=Unknown` 可全关回退旧行为。
- 新配置默认值 = 旧行为，无数据迁移，回滚删配置即可。