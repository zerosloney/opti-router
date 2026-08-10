# 设计：Thompson 连续/分级奖励

Task: 08-10-thompson-routing

## 问题根因

`OutcomeRecorder.RecordThompsonOutcome(modelName, bool isGood)` 只接收二值。成功路径调用点把 `elapsedMs < target` 压成 bool，失败路径传 `false`。幅度信息（快 vs 慢成功）在进入 `ThompsonStateStore` 前丢失。

## 方案：签名携带延迟幅度，奖励函数分级

### 签名变更

`RecordThompsonOutcome(string modelName, long? elapsedMs)`

- `elapsedMs == null` → **硬失败**（网络/超时/上游错误/被取消），奖励 0。
- `elapsedMs < target` → **快成功**，奖励 1.0。
- `elapsedMs >= target` → **慢成功**，部分奖励（见下）。

### 调用点映射

| 当前调用 | 新调用 |
|---------|--------|
| 成功路径 `RecordThompsonOutcome(m, elapsedMs < target)` | `RecordThompsonOutcome(m, attemptSw.ElapsedMilliseconds)` |
| 失败路径 `RecordThompsonOutcome(m, false)` | `RecordThompsonOutcome(m, null)` |

所有成功调用点已持有 `attemptSw.ElapsedMilliseconds`（已核实），无需新增计时。失败路径统一传 `null`。

### 奖励函数（分级，非连续线性）

在 `ThompsonStateStore.RecordOutcome` 内新增重载 `RecordOutcome(string modelName, double reward, double discountFactor)`：

| 情形 | reward |
|------|--------|
| 快成功（elapsed < target） | 1.0 |
| 慢成功（elapsed >= target） | 0.3（部分正面：仍成功但偏慢，轻微正信号）|
| 硬失败（null / elapsed 超时） | 0.0 |

更新逻辑（用 reward 替代二值）：
```csharp
stats.Alpha = stats.Alpha * factor + reward;
stats.Beta  = stats.Beta  * factor + (1.0 - reward);
```

- reward=1.0 → Alpha+1（等价旧快成功）。reward=0.0 → Beta+1（等价旧失败）。reward=0.3 → 慢成功部分正信号：Alpha+0.3、Beta+0.7。
- 保留旧二值重载 `RecordOutcome(modelName, bool isGood, discountFactor)` 委托到新重载（`isGood ? 1.0 : 0.0`），供测试/兼容。

### 阈值归一

`target` 来自 `OutcomeRecorder` 读取 `Routing.ThompsonLatencyTargetMs`（已有）。为避免在 store 内重复读取配置，`reward` 计算放在 `OutcomeRecorder.RecordThompsonOutcome`：

```csharp
public void RecordThompsonOutcome(string modelName, long? elapsedMs)
{
    var routing = _options.CurrentValue.Routing;
    double reward = elapsedMs switch
    {
        null => 0.0,
        var ms when ms < routing.ThompsonLatencyTargetMs => 1.0,
        _ => 0.3
    };
    _tsStore.RecordOutcome(modelName, reward, routing.ThompsonDiscountFactor);
}
```

`ThompsonStateStore` 保持纯状态机，不依赖配置。

## 兼容性

- `RecordOutcome(bool)` 旧重载保留并委托，现有测试不破坏。
- `EnableThompsonSampling` 默认关闭；`ThompsonDiscountFactor`/`ThompsonLatencyTargetMs` 校验不变。
- `LatencyAwarePolicy.ReorderByThompsonSampling` 只读 Alpha/Beta，无需改。
- 慢成功从「Beta 惩罚」变为「部分 Alpha+部分 Beta」——**行为变化**（根治型，接受）。

## 风险

- 慢成功 0.3 是经验值；用过小则慢成功退化为接近失败，过大则模糊快/慢边界。0.3 取「明显弱于快成功但非惩罚」。
- 需核对所有 18 个调用点的成功/失败分支映射正确（尤其 Fusion/Cascade/Race 的 was-cancelled 与 failed 分支，均传 null）。

## Rollback

- 单文件（`ThompsonStateStore`/`OutcomeRecorder`）+ 调用点机械替换。回滚即还原 bool 签名与 switch。无配置迁移。