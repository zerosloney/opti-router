# 区分竞速失败与真失败的 Thompson 奖励

Task: 08-10-race-vs-real-failure

## Goal

修复 Thompson 采样把「竞速失败」（race-cancelled：被更快模型比下去，模型本身未必坏）与「真失败」（现实故障）都记为同一硬失败（reward 0.0）的缺陷。目标：给竞速失败尝试一个**独立的部分奖励**，既不完全惩罚（模型只是慢、未必坏），也不像慢成功那样给满正反馈。

## Background（已核实，接续 08-10-single-model-routing 的 thompson-routing child）

- 连续奖励契约（上一任务已落地）：`OutcomeRecorder.RecordThompsonOutcome(string, long? elapsedMs)` → reward 映射——`null`=0.0 硬失败、`< ThompsonLatencyTargetMs`=1.0 快成功、`>= target`=0.3 慢成功；`ThompsonStateStore.RecordOutcome(string, double reward, double)` 用 `Alpha×discount+reward` / `Beta×discount+(1-reward)`。
- **缺陷点**：三类「非成功」目前都记 `null → 0.0`：
  1. `RaceOrchestrator.cs:176` `cancelledByRace` 分支（另一模型已胜出，本模型被取消）→ `RecordThompsonOutcome(model.Name, null)`。
  2. `RaceOrchestrator.cs:284` post-break 被取消分支 → `RecordThompsonOutcome(m.Name, null)`。
  3. `RaceOrchestrator.cs:203` 真实失败分支（上游错误/超时）→ `RecordThompsonOutcome(model.Name, null)`。
- 竞速失败（1、2）在语义上「模型仍在途、只是被比下去」，不应与崩溃/超时（3）同等惩罚——但当前共享同一 0.0 奖励。
- FusionRouter panel 超时（`panelTimedOut`）是 per-panel CTS 超时，非竞速取消，属另一类（本次范围见 Out of Scope）。

## Requirements

- R1 竞速失败获得独立部分奖励：`cancelledByRace` 与 post-break 被取消的尝试，reward 应为部分正值（介于硬失败 0.0 与慢成功 0.3 之间，或与慢成功同级），与真失败 0.0 可区分。
- R2 真失败（上游错误/超时/崩溃）保持 reward 0.0，语义不变。
- R3 反馈链契约保持：`ThompsonStateStore.RecordOutcome` 的 reward 公式不变；仅上游（`OutcomeRecorder` 与调用点）区分竞速失败。
- R4 默认配置不变（`EnableThompsonSampling=false` 默认关闭）；开启后行为变化（根治型）。
- R5 现有 Thompson 测试（多态奖励、折扣钳制、二值委托、LatencyAware+Thompson 重排）保持全绿。

## Acceptance Criteria

- [ ] 竞速失败（race-cancelled）的 reward ≠ 真失败的 reward（0.0），且为部分正值。
- [ ] 真失败仍为 0.0。
- [ ] 新增测试锁定：竞速失败独立奖励、与真失败区分、`ThompsonStateStore` 公式不变。
- [ ] 现有 Thompson/多态奖励测试全绿；`dotnet build` 通过。
- [ ] 无公共接口（`IRouterPolicy`/`RouterDecision`/`RouterContext`）签名变更。

## Out of Scope

- FusionRouter 的 panel/analyst/outer 超时（`panelTimedOut` 等 per-stage 超时）——它们是超时语义，非竞速取消，本次不改（后续可单独评估）。
- FusionRouter 中因 panel 全失败回退串行的路径。
- 改变 `ThompsonStateStore` 的 reward 公式与折扣逻辑。
- 引入成本/质量维度。

## Open Questions

- 竞速失败的部分奖励值定多少（0.15~0.5）？与慢成功 0.3 的相对大小——设计阶段定夺。