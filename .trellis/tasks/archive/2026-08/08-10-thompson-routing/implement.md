# 执行：Thompson 连续/分级奖励

Task: 08-10-thompson-routing

## Checklist

1. 修改 `src/OptiRouter/Routing/ThompsonStateStore.cs`：
   - `RecordOutcome(string, bool, double)` → 改为委托到新 `RecordOutcome(string, double reward, double)`（`isGood ? 1.0 : 0.0`）。
   - 新重载：`Alpha = Alpha×factor + reward`，`Beta = Beta×factor + (1-reward)`。
2. 修改 `src/OptiRouter/Endpoints/OutcomeRecorder.cs`：
   - `RecordThompsonOutcome(string, bool)` → `RecordThompsonOutcome(string, long? elapsedMs)`，内部按快/慢/失败映射 reward（1.0/0.3/0.0）。
3. 机械替换全部调用点（`ProxyOrchestrator`/`CascadeUpgradeHandler`/`FusionRouter`/`RaceOrchestrator`）：
   - 成功路径：`RecordThompsonOutcome(m, elapsedMs < target)` → `RecordThompsonOutcome(m, attemptSw.ElapsedMilliseconds)`。
   - 失败路径：`RecordThompsonOutcome(m, false)` → `RecordThompsonOutcome(m, null)`。
   - 用 `grep 'RecordThompsonOutcome' src/` 全量核对，确保无遗漏。
4. 更新测试 `MultiDimensionalAndBanditTests.cs`：
   - 新增：快成功 reward 1.0 / 慢成功 0.3 / 硬失败 0.0 三态区分。
   - 新增：慢成功同时增加 Alpha 与 Beta（reward 0.3 → Alpha+0.3, Beta+0.7）。
   - 保留折扣钳制测试（新重载路径）。
5. 更新 `.trellis/spec/backend/routing.md` 的 Thompson Outcome Recording 段。

## Validation

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~MultiDimensionalAndBanditTests"
dotnet test OptiRouter.sln -c Release
```

## Risky Files

- `src/OptiRouter/Routing/ThompsonStateStore.cs`
- `src/OptiRouter/Endpoints/OutcomeRecorder.cs`
- 4 个编排器调用点文件（ProxyOrchestrator/CascadeUpgradeHandler/FusionRouter/RaceOrchestrator）

## Review Gates

- `grep 'RecordThompsonOutcome' src/` 无残留 bool 调用。
- `dotnet build` + 全量测试绿。
- 快/慢/失败三态测试覆盖 reward 映射。