# 竞速失败奖励提为运行时配置

Task: 08-10-race-cancelled-reward-config

## Goal

把 `RaceCancelledReward`（当前为 `OutcomeRecorder` 内的 `private const 0.5` 编译期常量）提为 `RoutingOptions` 可运行时配置项，使其能像 `ThompsonDiscountFactor` 一样在 `appsettings.json` / 环境变量中配置、经 reload 热生效，从而可按观测效果独立调参。同时补全 `analyze_audit.py` 对 `cancelled-by-race` 信号的聚合，形成调参的观测闭环。

## Background（已核实）

- `OutcomeRecorder.cs:244`：`private const double RaceCancelledReward = 0.5;`——编译期常量，改值需改码 + 重新 build，不是运行时配置。
- `RecordThompsonRaceCancelled`（OutcomeRecorder.cs:251-255）用该常量：`_tsStore.RecordOutcome(modelName, RaceCancelledReward, routing.ThompsonDiscountFactor)`。
- 同类已可配置项（`RoutingOptions` + `RouterOptionsValidator` 校验 + reload 热生效）：`ThompsonDiscountFactor`（默认 0.95，校验 [0.5,0.99]）、`ThompsonLatencyTargetMs`（默认 800，校验 >0）。
- `analyze_audit.py build_by_reason` 的 keywords 表已含 `code-complex`/`code-simple`/`math-detected`/`translation-request` 等，但**不含** `cancelled-by-race`——竞速失败无法按模型聚合观测（`routing_reason` 含 `fusion: cancelled-by-race` 片段）。

## Requirements

- R1 新增 `RoutingOptions.ThompsonRaceCancelledReward`（默认 0.5），替代 `OutcomeRecorder` 的 `private const`。
- R2 `RouterOptionsValidator` 校验其范围（建议 `[0, 1]`，与 reward 语义一致；开启 Thompson Sampling 时校验）。
- R3 `OutcomeRecorder.RecordThompsonRaceCancelled` 改读配置项（`_options.CurrentValue.Routing.ThompsonRaceCancelledReward`），reload 热生效。
- R4 `analyze_audit.py build_by_reason` keywords 表加入 `cancelled-by-race`，使竞速失败可按模型聚合（配合已有 By Model 维度判断「取消率 vs 采纳后成功率」）。
- R5 默认配置行为不变（默认 0.5 与现 const 相同）；现有测试全绿。

## Acceptance Criteria

- [ ] `appsettings.json` / 环境变量可配置 `ThompsonRaceCancelledReward`，reload 后生效。
- [ ] 越界值（<0 或 >1）被校验拦截，与现有 Thompson 校验一致。
- [ ] `RecordThompsonRaceCancelled` 使用配置值（默认 0.5），不再用 const。
- [ ] `analyze_audit.py` 能按 `cancelled-by-race` 分组聚合（不再落入 default）。
- [ ] 现有测试全绿；`dotnet build` 通过；无公共路由接口签名变更。

## Out of Scope

- 改变奖励曲线其它值（快成功 1.0 / 慢成功 0.3 / 硬失败 0.0——那些是语义基线，本次不动）。
- 引入更复杂的调参机制（如自动调参 / 强化学习）。
- 改动 `ThompsonStateStore` 公式。

## Open Questions

- 校验范围用 `[0,1]` 还是更窄（如 `[0.1,0.9]`）？——设计阶段定夺（默认建议 [0,1]，与 reward 可能取值一致）。