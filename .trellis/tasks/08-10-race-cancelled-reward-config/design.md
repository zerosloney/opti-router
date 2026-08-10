# 设计：竞速失败奖励提为运行时配置

Task: 08-10-race-cancelled-reward-config

## 问题根因

`RaceCancelledReward = 0.5` 是 `OutcomeRecorder` 的 `private const`（编译期常量），调参必须改码重构。同类配置 `ThompsonDiscountFactor`/`ThompsonLatencyTargetMs` 已是运行时配置项，唯独竞速失败奖励漏了。且 `analyze_audit.py` 未聚合 `cancelled-by-race`，无观测依据。

## 方案

### 1. 新增配置项（`RoutingOptions`）

在 `ThompsonLatencyTargetMs` 之后加：

```csharp
/// <summary>
/// 竞速失败（并行 racing 中被更快模型比下去而取消）的 Thompson 部分奖励。
/// 取值范围 [0.0, 1.0]（由 RouterOptionsValidator 强制）；0.0=硬失败等效，1.0=快成功等效。
/// 默认 0.5：高于慢成功 0.3、低于快成功 1.0。可独立调参，按观测效果调整。
/// </summary>
public double ThompsonRaceCancelledReward { get; set; } = 0.5;
```

### 2. 校验（`RouterOptionsValidator`）

在 Thompson 校验块内（`EnableThompsonSampling` 分支）追加：

```csharp
if (options.Routing.ThompsonRaceCancelledReward < 0.0 || options.Routing.ThompsonRaceCancelledReward > 1.0)
{
    return ValidateOptionsResult.Fail("Routing.ThompsonRaceCancelledReward 必须在 [0.0, 1.0] 范围内（启用 Thompson Sampling 时）。");
}
```

范围 `[0,1]` 与 reward 语义一致（reward 在 `ThompsonStateStore` 内被 `Math.Clamp(0,1)`）。

### 3. 消费（`OutcomeRecorder`）

- 删除 `private const double RaceCancelledReward = 0.5;`。
- `RecordThompsonRaceCancelled` 改读配置：

```csharp
public void RecordThompsonRaceCancelled(string modelName)
{
    var routing = _options.CurrentValue.Routing;
    _tsStore.RecordOutcome(modelName, routing.ThompsonRaceCancelledReward, routing.ThompsonDiscountFactor);
}
```

`_options` 是 `IOptionsMonitor<RouterOptions>`，reload 热生效（与 `RecordThompsonOutcome` 读 `ThompsonDiscountFactor` 同机制）。

### 4. 观测闭环（`analyze_audit.py`）

`build_by_reason` 的 keywords 表加入 `"cancelled-by-race"`。`routing_reason` 含 `fusion: cancelled-by-race` / `fusion: cancelled-by-race (post-break)` 片段，均可命中。配合已有 By Model 维度，可观测「某模型 cancelled-by-race 频率 vs 被采纳时成功率」。

## 兼容性

- 默认 0.5 与现 const 一致，行为不变。
- `EnableThompsonSampling=false`（默认）：不影响（校验在开启分支内，消费读默认值但策略不采样）。
- reload 热生效，无需重启。
- 无公共路由接口（`IRouterPolicy`/`RouterDecision`/`RouterContext`）变更。

## 风险

- 移除 `private const` 后，若测试直接引用该常量会编译失败——需检查（现有测试通过 `RecordThompsonRaceCancelled` 行为断言，不直接引用常量，已核实）。
- `[0,1]` 允许 0.0（等效硬失败）与 1.0（等效快成功）——语义上允许用户把竞速失败调成任意档，文档注明。

## Rollback

- 单配置项 + 校验 + 消费点 + 脚本 keyword。回滚即还原 const 与删除配置项。无配置迁移、无 schema 变更。