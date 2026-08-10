# 设计：多维能力评分维度化 tier 回退

Task: 08-10-capability-scoring

## 问题根因

`ModelEndpointOptions.GetEffectiveCapability(dimension)` 对所有未显式配置的维度回退到同一 `Tier switch` 值（Strong 0.9/Medium 0.6/Cheap 0.3）。这使「语言」这种廉价维度与「推理」这种昂贵维度使用同样的档距，导致语言任务（simple-qa/translation）下 Strong 的分量贡献 0.9 远高于 Cheap 的 0.3，分桶后 Cheap 永不胜出。

## 方案：按维度区分 tier 回退

将单一 tier 回退改为**每维度回退表**。维度分两类：

| 维度 | 语义 | Strong | Medium | Cheap | 档距 |
|------|------|--------|--------|-------|------|
| `language` | 廉价维度，模型间差距小 | 0.80 | 0.78 | 0.76 | ~0.02（近扁平）|
| `reasoning` | 昂贵维度，强推理是核心差异 | 0.90 | 0.50 | 0.20 | 0.30-0.40（陡）|
| `coding` | 昂贵维度 | 0.90 | 0.60 | 0.30 | 0.30（陡）|

### 实现位置

`ModelEndpointOptions.GetEffectiveCapability` 内的 tier 回退，从单一 `switch` 改为查维度表：

```csharp
private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<ModelTier, double>> DimensionFallbacks =
    new Dictionary<string, IReadOnlyDictionary<ModelTier, double>>(StringComparer.OrdinalIgnoreCase)
    {
        ["language"]  = new Dictionary<ModelTier, double> { [Strong]=0.80, [Medium]=0.78, [Cheap]=0.76 },
        ["reasoning"] = new Dictionary<ModelTier, double> { [Strong]=0.90, [Medium]=0.50, [Cheap]=0.20 },
        ["coding"]    = new Dictionary<ModelTier, double> { [Strong]=0.90, [Medium]=0.60, [Cheap]=0.30 },
    };

public double GetEffectiveCapability(string dimension)
{
    if (Capabilities is not null && Capabilities.TryGetValue(dimension, out var val))
        return val;
    if (DimensionFallbacks.TryGetValue(dimension, out var byTier) && byTier.TryGetValue(Tier, out var fb))
        return fb;
    // 未知维度：保守回退 0.5（不偏向任何档）
    return 0.5;
}
```

未知维度（非 coding/reasoning/language）回退 0.5，避免未知维度被误判为任意档优势。

### 效果验证（数学推导）

**simple-qa**（`language=1.0, reasoning=0.1`）：
- Strong: 1.0×0.80 + 0.1×0.90 = 0.89
- Medium: 1.0×0.78 + 0.1×0.50 = 0.83
- Cheap: 1.0×0.76 + 0.1×0.20 = 0.78
- 桶 floor(/0.15)：Strong=5, Medium=5, Cheap=5 → 同桶 → 价格升序 → **Cheap 胜**。✅

**math**（`reasoning=1.0, coding=0.5, language=0.3`）：
- Strong: 1.0×0.90 + 0.5×0.90 + 0.3×0.80 = 1.59
- Cheap: 1.0×0.20 + 0.5×0.30 + 0.3×0.76 = 0.578
- 桶 floor(/0.15)：Strong=10, Cheap=3 → gap 大 → **Strong 胜**。✅

### 容差与分桶保持

`CapabilityScoreTolerance = 0.15` 与 `floor(score/0.15)` 分桶机制**不变**。维度化回退使廉价维度近扁平，从而在语言任务上自然落入同桶；昂贵维度陡峭分桶。无需改容差逻辑。

## 兼容性

- `EnableMultiDimensionalRouting=false`（默认）：`RuleClassifierPolicy.Apply` 走 tier 过滤分支，不触碰 `GetEffectiveCapability`——**行为不变**。
- 显式配置 `Capabilities` dict：仍优先，不受影响。
- `GetEffectiveCapability` 签名不变（公共 API 无破坏）。
- 权重 profile 与维度名不变。

## 风险

- 依赖现有 `MultiDimensionalAndBanditTests` 的 close-scores/gap 断言。这些测试用 `TestHelpers.BuildOptions` 构造模型，可能未配 `Capabilities`，将走维度回退——需核对断言数值是否随新回退变化，必要时更新断言并说明新语义。
- 未知维度回退 0.5 是新增行为，需测试锁定。

## Rollback

- 单一方法内改动，回滚即还原 `GetEffectiveCapability` 的 switch。无配置迁移、无 schema 变更。