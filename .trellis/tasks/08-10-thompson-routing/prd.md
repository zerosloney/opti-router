# 强化 Thompson 学习路由（thompson-routing）

Child of: 08-10-single-model-routing

## Goal

将 Thompson Sampling 的**二值反馈**升级为**连续/分级奖励**，让学习信号携带延迟幅度（区分「快成功」「慢成功」「失败」），从而更精准地学到「哪个模型在请求特征下又稳又快」。同时保持 `EnableThompsonSampling` 默认关闭与既有配置语义。

## Background（已核实）

- 反馈链路：各编排器（`ProxyOrchestrator`/`CascadeUpgradeHandler`/`FusionRouter`/`RaceOrchestrator`）调用 `OutcomeRecorder.RecordThompsonOutcome(modelName, isGood)`，其中 `isGood = elapsedMs < ThompsonLatencyTargetMs`。
- `OutcomeRecorder.RecordThompsonOutcome` → `ThompsonStateStore.RecordOutcome(modelName, isGood, discountFactor)`。
- `ThompsonStateStore.RecordOutcome`：`Alpha = Alpha×discount + (isGood?1:0)`，`Beta = Beta×discount + (isGood?0:1)`。
- 消费：`LatencyAwarePolicy.ReorderByThompsonSampling` 读 `Alpha/Beta`，`_sampleBeta(alpha,beta)` 采样排序。
- 调用点均已有 `attemptSw.ElapsedMilliseconds` 可用（无需额外计时）。
- 现有测试：`MultiDimensionalAndBanditTests` 的 Thompson 段（Beta 采样、状态折扣、LatencyAware+Thompson 重排）。

### 缺陷

二值 `isGood` 丢失幅度：100ms 与 790ms（target 800）的成功记作相同的 +1 Alpha；失败一律 +1 Beta，不区分「慢成功」与「硬失败」。学习分辨率低，无法反映「快模型」与「勉强达标模型」的差异。

## Requirements

- R2.1 连续/分级奖励：慢成功（elapsed ≥ target 但仍成功）应获得**部分正面**奖励（< 1.0），而非与硬失败同等的 Beta 惩罚或与快成功同等的满 Alpha。
- R2.2 保持 `RecordThompsonOutcome` 调用点契约：所有调用点已传 `elapsedMs`，升级签名让它接收幅度（而非 bool）。
- R2.3 `EnableThompsonSampling=false` 默认关闭；`ThompsonDiscountFactor`/`ThompsonLatencyTargetMs` 校验与语义保持。
- R2.4 现有 Thompson 测试（Beta 采样、折扣钳制、重排）语义保持；新增连续奖励测试。

## Acceptance Criteria

- [ ] 快成功（elapsed < target）奖励 > 慢成功（elapsed ≥ target）奖励 > 硬失败。
- [ ] 慢成功不再与硬失败同等惩罚（慢成功部分正面）。
- [ ] 折扣因子钳制、Beta 采样、LatencyAware+Thompson 重排既有测试全绿。
- [ ] 新增测试：连续奖励曲线、慢成功 vs 硬失败区分、折扣钳制兼容。
- [ ] 无公共接口（`IRouterPolicy`/`RouterDecision`/`RouterContext`）签名变更；`ThompsonStateStore` 内部 API 可演进。

## Out of Scope

- 引入成本/输出质量维度（本次仅延迟幅度；成本维度留待后续）。
- 改变 `ThompsonSampler` 的 Beta 采样算法。
- 改变 `LatencyAwarePolicy` 的采样排序逻辑（除非必要的最小适配）。

## Open Questions

- 奖励函数形态：连续线性（`1 - elapsed/target` 截断）还是分级（fast=1.0 / slow=0.3 / fail=0.0）？——设计阶段定夺。