# 单模型路由实现 P1+P2+P3 — 技术设计

## 1. P1 分类准确率可观测

### 1.1 结构化分类信号

`RouterDecision` 新增结构化字段（不破坏现有 `Reason` 字符串）：

```csharp
// RouterDecision 新增
public string? ClassificationSignal { get; init; }   // "code-complex" / "simple-qa" / ...
public ModelTier? ClassificationTargetTier { get; init; }  // 信号应路由的 tier
```

- `RuleClassifierPolicy.Apply` 在设置 `Reason` 的同时填充这两个字段（`target=Tier(signal)` 的解析来源）。
- `routing_reason` 的 `target=Tier(signal)` 格式**保持不变**（向后兼容 `analyze_audit.py`）。
- 结构化字段供生产端直接读取（不解析字符串），`analyze_audit.py` 继续用字符串解析（离线）。

### 1.2 契约

- 新增字段默认 null（未分类/未启用时），不改变现有行为。
- 测试锁定：`RuleClassifierPolicy` 对 7 类信号均填充 `ClassificationSignal` + `ClassificationTargetTier`，且 `Reason` 含 `target=Tier(signal)`。

## 2. P2 策略链分组契约 + 结构化 Reason

### 2.1 勘察结论：策略链本质上串行

逐策略勘察发现，策略链**不可安全并行**：
- **FailoverPolicy**：过滤 + **fallback 链补充**（全部排除时从 `AllModels` 补降级候选）——非纯谓词，有副作用。
- **QuotaAwarePolicy**：过滤（排除耗尽）+ **重排**（headroom 不足降级到尾部）——既过滤又重排。
- 其余策略（CapabilityFilter/LongInput 纯过滤、RuleClassifier/SemanticRouter 分类覆盖、LatencyAware/PromptCacheAffinity 排序、BudgetGuard/SessionAffinity/LoadBalance 约束）各自依赖 `previous.Candidates` 的**前一步输出**。
- 测试大量断言 `Reason` 字符串（`Assert.Contains("budget-guard:", result.Reason)` 等 40+ 处）。

**结论**：current 链是「每策略变换 Candidates」的串行流水线，genuine 并行需重构为独立子链（谓词组合），高风险。**P2 采取保守正确方案**：

### 2.2 交付：分组契约 + 结构化 Reason（不强制并行）

- **`PolicyGroup` 枚举 + `IRouterPolicy.Group` 属性**：策略显式声明所属分组（Filter/Classify/Order/Constraint），建立分组契约（未来并行化的地基）。
- **`RouterDecision.ReasonEvents`**：结构化事件列表（`ReasonEvent(Policy, Detail)`），`Reason` 字符串保持原样（不破坏 40+ 测试断言）。
- **`RouterEngine.Decide` 分组感知执行**：按组依赖序（Filter→Classify→Order→Constraint）执行，组内保持串行（保留叠加过滤/fallback/重排语义）。**不做并行**——诚实结论：并行需重构独立子链，超出安全范围，留作未来工作。

> **为何不并行**：并行要求组内策略是「可组合纯谓词」或「后覆盖」，但 Failover 有 fallback 副作用、QuotaAware 有重排副作用，组合会改变语义；且测试锁定 `Reason` 字符串顺序。强行并行风险 >> 收益。分组契约先落地，并行化留给「独立子链重构」的独立任务。

### 2.3 结构化 Reason

```csharp
// RouterDecision 新增
public IReadOnlyList<ReasonEvent> ReasonEvents { get; init; } = Array.Empty<ReasonEvent>();
public sealed record ReasonEvent(string Policy, string Detail);
```

- 策略 `Apply` 在现有 `Reason` 字符串拼接**之外**，追加 `ReasonEvent`（结构化、机器可解析）。
- `Reason` 字符串保持原生成逻辑（不破坏测试）。
- 新消费者（P1/P3/未来可观测）读 `ReasonEvents`，不解析字符串。

## 3. P3 上下文老虎机（LinUCB）

### 3.1 配置（RoutingOptions + Validator）

```csharp
public bool EnableContextualBandit { get; set; } = false;   // 默认关
public double ContextualBanditAlpha { get; set; } = 1.0;    // UCB 探索系数 α
public double ContextualBanditDiscountFactor { get; set; } = 0.95;  // 历史折扣
```

- Validator：`EnableContextualBandit=true` 时 `ContextualBanditAlpha > 0`、`ContextualBanditDiscountFactor ∈ [0.5, 0.99]`。
- 与 `EnableThompsonSampling` 互斥（二选一）或共存（Thompson 保底）——设计为**互斥**（`EnableContextualBandit=true` 时 Thompson 段内被 LinUCB 替代），避免双重探索。

### 3.2 ContextualBanditState

```csharp
public sealed class ContextualBanditState
{
    // 每模型：A (d×d 协方差逆), b (d×1), 样本数
    public sealed class ArmState { public double[,] A; public double[] b; public int N; }
    private readonly ConcurrentDictionary<string, ArmState> _arms;
    public double[] Predict(string model, double[] context);   // θ·x
    public void Update(string model, double[] context, double reward, double discount);
    public int Retain(IEnumerable<string> retainNames);        // 热重载清理
}
```

- 特征维度 d = 分类信号 one-hot（7）+ tier one-hot（3）+ bias = 11。
- LinUCB 打分：`score = θ·x + α·sqrt(xᵀ A⁻¹ x)`。
- 更新：`A += x·xᵀ`，`b += reward·x`，`θ = A⁻¹·b`；折扣按 discount 缩放历史。
- 线程安全：`ConcurrentDictionary` + 每 arm 锁。

### 3.3 LatencyAwarePolicy 接入

```csharp
// ReorderSegment 内：
if (options.EnableContextualBandit)
    return ReorderByContextualBandit(segment, context);   // LinUCB 打分
else if (options.EnableThompsonSampling)
    return ReorderByThompsonSampling(segment);
else if (options.EnableLatencyAware)
    return ReorderByLatencyScore(segment, minSamples);
```

- `ReorderByContextualBandit`：从 `context.Request` 提取分类信号 → 构造上下文向量 → 每模型 `Predict` + UCB → 降序。
- 上下文特征来源：`RouterDecision.ClassificationSignal`（P1 新增）→ one-hot。
- 冷启动：样本不足（N < 阈值）时用均匀先验（θ=0，UCB 大 → 探索）。

### 3.4 奖励

- 复用现有 `OutcomeRecorder` 的 reward 语义（快成功 1.0/慢成功 0.3/失败 0/竞速 0.5），`ContextualBanditState.Update` 接收 reward。
- `OutcomeRecorder` 在记录 Thompson 奖励的同时，若 `EnableContextualBandit` 则同步更新 bandit state（需注入 `ContextualBanditState`）。

## 4. 兼容与回滚

- 所有新特性默认关：`EnableContextualBandit=false`、`ReasonEvents` 空、`ClassificationSignal` null → 行为与现有一致。
- P2 并行：任何组并行结果 != 串行则回退该组为串行（回归测试锁定）。
- `Reason` 字符串格式保持（`policy: detail; ...`），现有测试/日志/审计不破坏。
- 热重载：`ContextualBanditState.Retain` 清理已删模型（对齐 `ThompsonStateStore`）。

## 5. 风险

- **P2 并行正确性**：组内策略隐式依赖（读彼此输出）会导致并行结果 != 串行。缓解：只对可证明无依赖的策略并行，回归测试锁定，不一致即回退串行。
- **P3 特征维度**：one-hot 11 维，冷启动需样本。缓解：样本不足用均匀先验探索。
- **P3 与 Thompson 互斥**：`EnableContextualBandit=true` 时 Thompson 不生效，需文档明示。
- **Reason 格式回归**：现有测试断言 Reason 字符串，需保持 `policy: detail` 格式。