# 设计：竞速失败独立部分奖励

Task: 08-10-race-vs-real-failure

## 问题根因

`RaceOrchestrator` 的竞速失败（`cancelledByRace` 与 post-break 被取消）与真失败都调用 `RecordThompsonOutcome(modelName, null)` → reward 0.0。竞速失败是「模型仍在途、被更快者比下去」，语义上不应与崩溃/超时同等惩罚。

## 方案：专用方法 + 命名常量奖励

### 奖励曲线（`OutcomeRecorder` 内命名常量）

| 情形 | 调用 | reward |
|------|------|--------|
| 快成功（elapsed < target） | `RecordThompsonOutcome(m, elapsedMs)` | 1.0 |
| 慢成功（elapsed >= target） | `RecordThompsonOutcome(m, elapsedMs)` | 0.3 |
| **竞速失败（被取消）** | **`RecordThompsonRaceCancelled(m)`** | **0.5** |
| 硬失败（真故障） | `RecordThompsonOutcome(m, null)` | 0.0 |

竞速失败取 0.5：明确高于硬失败 0.0、高于慢成功 0.3（被取消不代表慢——可能很快只是运气差），低于快成功 1.0。值独立可调（命名常量），与慢成功区分便于未来审计/调参。

### 实现

`OutcomeRecorder` 新增：

```csharp
/// <summary>竞速失败（被更快模型比下去而取消）的部分奖励。独立于慢成功(0.3)与硬失败(0.0)。</summary>
private const double RaceCancelledReward = 0.5;

/// <summary>
/// 上报竞速失败反馈：模型在并行竞速中被更快者比下去而取消，非自身故障。
/// 计部分正奖励（<see cref="RaceCancelledReward"/>），不完全惩罚。
/// </summary>
public void RecordThompsonRaceCancelled(string modelName)
{
    var routing = _options.CurrentValue.Routing;
    _tsStore.RecordOutcome(modelName, RaceCancelledReward, routing.ThompsonDiscountFactor);
}
```

`RaceOrchestrator` 两处取消分支（`cancelledByRace` 与 post-break 被取消）改调 `RecordThompsonRaceCancelled(...)`；真失败分支保持 `RecordThompsonOutcome(name, null)`。

### 边界

- 竞速失败分支当前 `elapsedMs` 在取消时可能不准确（请求被中止），故不沿 `elapsedMs` 映射，而是固定部分奖励——语义清晰。
- 配额失败（`quotaLimited`）路径不调 Thompson（与既有契约一致），保持不动。
- `ThompsonStateStore.RecordOutcome` 公式、折扣、钳制**不变**（R3）。

## 兼容性

- `EnableThompsonSampling=false` 默认关闭：`LatencyAwarePolicy` 不采样，行为不变。
- 开启后：竞速失败从 0.0 → 0.5，行为变化（根治型，接受）。
- `RecordThompsonOutcome(string, long?)` 签名不变；新增独立方法。公共路由接口（`IRouterPolicy` 等）不变。

## 风险

- 0.5 是经验值；若过接近慢成功 0.3，区分度低；过接近 1.0 则竞速失败被过度奖赏。0.5 取中间偏保守。
- 需确认 `RaceOrchestrator` 两处取消分支与真失败分支的区分变量（`cancelledByRace`、`postBreakQuotaLimited`）判断正确，避免把真失败误记成竞速失败。

## Rollback

- 单方法 + RaceOrchestrator 两处调用点改动。回滚即还原为 `RecordThompsonOutcome(name, null)`。无配置迁移、无 schema 变更。