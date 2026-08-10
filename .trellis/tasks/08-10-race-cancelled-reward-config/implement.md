# 执行：竞速失败奖励提为运行时配置

Task: 08-10-race-cancelled-reward-config

## Checklist

1. `src/OptiRouter/Configuration/RoutingOptions.cs`：`ThompsonLatencyTargetMs` 后新增 `ThompsonRaceCancelledReward`（默认 0.5）。
2. `src/OptiRouter/Configuration/RouterOptionsValidator.cs`：Thompson 校验块内追加 `[0.0, 1.0]` 校验。
3. `src/OptiRouter/Endpoints/OutcomeRecorder.cs`：删除 `RaceCancelledReward` const，`RecordThompsonRaceCancelled` 改读 `routing.ThompsonRaceCancelledReward`。
4. `scripts/analyze_audit.py`：`build_by_reason` keywords 表加入 `"cancelled-by-race"`。
5. 更新测试：
   - `MultiDimensionalAndBanditTests.RecordThompsonRaceCancelled_*`：确认仍用默认 0.5（行为不变）；新增一个用自定义配置值验证 reload 生效的用例（构造 `RouterOptions` 设 `ThompsonRaceCancelledReward=0.7`，断言 store 状态）。
   - `RouterOptionsBindingTests`：如已有 Thompson 配置绑定用例，补新键。
6. 更新 `.trellis/spec/backend/routing.md`（Thompson 段 + Config Keys 表加新键）与 README（如需）。

## Validation

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~MultiDimensionalAndBanditTests|FullyQualifiedName~RouterOptionsBindingTests"
dotnet test OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~EndToEndSmokeTests"
```

脚本验证：构造含 `cancelled-by-race` 的 reason 样例，确认 `build_by_reason` 不再落入 default。

## Risky Files

- `src/OptiRouter/Configuration/RoutingOptions.cs`
- `src/OptiRouter/Configuration/RouterOptionsValidator.cs`
- `src/OptiRouter/Endpoints/OutcomeRecorder.cs`
- `scripts/analyze_audit.py`
- `tests/OptiRouter.Tests/Routing/MultiDimensionalAndBanditTests.cs`

## Review Gates

- `grep 'RaceCancelledReward' src/` 无残留 const 引用。
- `grep 'ThompsonRaceCancelledReward' src/` 命中 RoutingOptions 定义 + 校验 + OutcomeRecorder 消费。
- 配置值 reload 生效有测试锁定。
- `dotnet build` + 全量测试绿。
- `analyze_audit.py` 能按 `cancelled-by-race` 分组。