# 执行：竞速失败独立部分奖励

Task: 08-10-race-vs-real-failure

## Checklist

1. `src/OptiRouter/Endpoints/OutcomeRecorder.cs`：
   - 新增常量 `RaceCancelledReward = 0.5`。
   - 新增方法 `RecordThompsonRaceCancelled(string modelName)` → `_tsStore.RecordOutcome(modelName, RaceCancelledReward, routing.ThompsonDiscountFactor)`。
2. `src/OptiRouter/Endpoints/RaceOrchestrator.cs`：
   - `cancelledByRace` 分支（约 L176）：`RecordThompsonOutcome(model.Name, null)` → `RecordThompsonRaceCancelled(model.Name)`。
   - post-break 被取消分支（约 L284）：`RecordThompsonOutcome(m.Name, null)` → `RecordThompsonRaceCancelled(m.Name)`。
   - 真失败分支（约 L203）保持 `RecordThompsonOutcome(model.Name, null)` 不动。
   - 配额失败分支不动（不调 Thompson）。
3. 更新测试 `MultiDimensionalAndBanditTests.cs`：
   - 新增：`RecordThompsonRaceCancelled` 记 reward 0.5（Alpha+0.5, Beta+0.5 经折扣）。
   - 新增：竞速失败 reward ≠ 硬失败 reward（0.5 vs 0.0），且 < 快成功 1.0。
   - 既有多态奖励/折扣钳制/二值委托/重排测试保持。
4. 更新 `.trellis/spec/backend/routing.md` 的 Thompson Outcome Recording 段（奖励曲线加竞速失败行）。

## Validation

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~MultiDimensionalAndBanditTests"
dotnet test OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~EndToEndSmokeTests"
```

## Risky Files

- `src/OptiRouter/Endpoints/OutcomeRecorder.cs`（新方法 + 常量）
- `src/OptiRouter/Endpoints/RaceOrchestrator.cs`（两处取消分支迁移）
- `tests/OptiRouter.Tests/Routing/MultiDimensionalAndBanditTests.cs`

## Review Gates

- `grep 'RecordThompsonRaceCancelled' src/` 命中 OutcomeRecorder 定义 + RaceOrchestrator 恰好 2 处调用。
- 真失败分支仍 `null → 0.0`（grep 确认 L203 未改）。
- `dotnet build` + 全量测试绿。
- 无公共路由接口签名变更。