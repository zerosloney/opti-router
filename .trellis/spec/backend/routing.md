# Routing

> Multi-model routing engine, policy chain, multi-dimensional capability routing, and Thompson Sampling MAB.

---

## 1. Scope / Trigger

- Routing engine (`RouterEngine`) orchestrates a chain of `IRouterPolicy` policies to produce a ranked candidate list.
- Policy chain order (defined in `Program.cs`): CapabilityFilter → RuleClassifier → SessionAffinity → SemanticRouter → LongInput → LatencyAware → PromptCacheAffinity → BudgetGuard → QuotaAware → Failover → LoadBalance.
- Multi-dimensional routing (`EnableMultiDimensionalRouting`) weights request dimensions against model capability scores.
- Thompson Sampling MAB (`EnableThompsonSampling`) adaptively reorders candidates by Beta-distribution sampling from historical latency/success data.

---

## 2. Signatures

### Core Types

```csharp
// Routing decision, flows through the policy chain (immutable records)
public sealed record RouterDecision
{
    public required IReadOnlyList<ModelEndpointOptions> Candidates { get; init; }
    public ModelEndpointOptions Primary => Candidates[0];
    public required string Reason { get; init; }
    public bool BudgetExhausted { get; init; }
    public int EstimatedInputTokens { get; init; }
    public RequestComplexity RequestComplexity { get; init; } = RequestComplexity.Unknown;
}

// Policy input context (immutable record)
public sealed record RouterContext
{
    public required ChatRequest Request { get; init; }
    public required IReadOnlyList<ModelEndpointOptions> AllModels { get; init; }
    public required RouterOptions Options { get; init; }
    public int EstimatedInputTokens { get; init; }
    public IReadOnlySet<string> FailedModels { get; init; }
    public string? SessionId { get; init; }
}
```

### Policy Interface

```csharp
public interface IRouterPolicy
{
    RouterDecision Apply(RouterContext context, RouterDecision previous);
}
```

### RouterEngine

```csharp
public sealed class RouterEngine
{
    public RouterDecision Decide(
        ChatRequest request,
        RouterOptions options,
        IReadOnlySet<string>? failedModels = null,
        string? sessionId = null);
}
```

### Thompson Sampling

```csharp
public static class ThompsonSampler
{
    // Production: thread-local RNG, no lock contention
    public static double SampleBeta(double alpha, double beta);

    // Test: seeded RNG for deterministic assertions
    public static double SampleBeta(double alpha, double beta, Random rng);
}

public sealed class ThompsonStateStore
{
    public ModelStats GetOrAdd(string modelName);
    public bool Remove(string modelName);
    public int Retain(IEnumerable<string>? retainNames);
    public void RecordOutcome(string modelName, bool isGood, double discountFactor);
}

public sealed class ModelStats
{
    public double Alpha { get; set; }   // Default: 1.0
    public double Beta { get; set; }    // Default: 1.0
    public readonly object Lock;
}
```

### Multi-Dimensional Capability Scoring

```csharp
// On ModelEndpointOptions:
public IDictionary<string, double> Capabilities { get; set; }
public double GetEffectiveCapability(string dimension);

// 维度化 tier 回退（Capabilities dict 无该维度条目时）：
// 语言是「廉价维度」档距近扁平，推理/代码是「昂贵维度」档距陡。
//   language:  Strong -> 0.80, Medium -> 0.78, Cheap -> 0.76
//   reasoning: Strong -> 0.90, Medium -> 0.50, Cheap -> 0.20
//   coding:    Strong -> 0.90, Medium -> 0.60, Cheap -> 0.30
// 未知维度（非 coding/reasoning/language）保守回退 0.5，不偏向任何档。
// 效果：语言任务（simple-qa/translation）下 Strong 与 Cheap 语言分数落入同 tolerance 桶 → 价格择廉；
//       推理/数学任务下 Strong 因推理分差显著胜出。
```

### Latency Stats

```csharp
public sealed record ModelLatencyStats(double AverageLatencyMs, double P95LatencyMs, int SampleCount);

public interface ILatencyStatsProvider
{
    ModelLatencyStats? GetStats(string modelName);
    void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats);
}

// Thread-safe implementation: volatile reference swap, O(1) reads
public sealed class LatencyStatsCache : ILatencyStatsProvider;
```

- `GetLatencyStatsSince`（`IRequestAuditStore`）返回 `IReadOnlyDictionary<string, ModelLatencyStats>`，SQLite 与 InMemory 实现按 model 收集成功延迟列表 → 排序 → avg + p95（线性插值，与 `scripts/analyze_audit.py` 的 percentile 语义一致）。
- 延迟评分：`score = 1 / (avg + 0.5×p95 + 50)`。p95 项压制「avg 稳但 tail 差」的模型。

---

## 3. Contracts

### Config Keys (RoutingOptions)

| Key | Type | Default | Validation |
|-----|------|---------|------------|
| `EnableMultiDimensionalRouting` | bool | `false` | — |
| `EnableThompsonSampling` | bool | `false` | — |
| `ThompsonDiscountFactor` | double | `0.95` | `[0.5, 0.99]` when `EnableThompsonSampling=true` |
| `ThompsonLatencyTargetMs` | double | `800.0` | `> 0` when `EnableThompsonSampling=true` |
| `ThompsonRaceCancelledReward` | double | `0.5` | `[0.0, 1.0]` when `EnableThompsonSampling=true` |
| `EnableLatencyAware` | bool | `false` | — |
| `LatencyMinSamples` | int | `10` | — |
| `LatencyStatsWindowMinutes` | int | `60` | — |
| `EnablePromptCacheAffinity` | bool | `false` | — |
| `PromptCacheAffinityTtlSeconds` | int | `600` | `> 0` |
| `EnableQuotaAwareRouting` | bool | `false` | — |
| `EnableDynamicFusionPanelSize` | bool | `false` | — |
| `FusionRouterMinPanelSize` | int | `2` | `[2, 5]` and `<= FusionRouterPanelSize` |
| `EnableFusionDiversity` | bool | `false` | — |
| `FusionRouterPanelTimeoutSeconds` | int | `0` | `>= 0` (`0` = disabled, backward-compatible) |
| `FusionRouterTemperature` | double | `0.0` | `[0, 2]` (analyst/outer 温度，低温保 JSON 稳定) |
| `FusionRouterPanelTemperature` | `double?` | `null` | `null` 沿用 `FusionRouterTemperature`；非 null 须 `[0, 2]` |
| `FusionRouterMinComplexity` | `RequestComplexity` | `Unknown` | 合法枚举值（默认 `Unknown`=无门控，向后兼容） |
| `EnableContextualBandit` | bool | `false` | 与 `EnableThompsonSampling` 互斥（启动期 `RouterOptionsValidator` 强制拒绝两者同开） |
| `ContextualBanditAlpha` | double | `1.0` | `> 0` when `EnableContextualBandit=true` |
| `ContextualBanditDiscountFactor` | double | `0.95` | `[0.5, 0.99]` when `EnableContextualBandit=true` |

### Thompson Outcome Recording

```csharp
// 成功路径（快/慢成功）：传 elapsedMs
RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds);
// 真失败（网络/超时/上游错误/崩溃）：传 null
RecordThompsonOutcome(candidate.Name, null);
// 竞速失败（并行 racing 中被更快者比下去而取消）：专用方法
RecordThompsonRaceCancelled(candidate.Name);
// → OutcomeRecorder 映射 reward，再调 _tsStore.RecordOutcome(modelName, reward, routing.ThompsonDiscountFactor)
```

- 连续/分级奖励（`RecordThompsonOutcome(string, long? elapsedMs)` → reward）：
  - `elapsedMs == null`（硬失败：网络/超时/上游错误）→ reward `0.0`
  - `elapsedMs < ThompsonLatencyTargetMs`（快成功）→ reward `1.0`
  - `elapsedMs >= ThompsonLatencyTargetMs`（慢成功）→ reward `0.3`（部分正反馈，成功但偏慢）
- 竞速失败（`RecordThompsonRaceCancelled(string, ...)`）→ reward `RoutingOptions.ThompsonRaceCancelledReward`（默认 0.5）：模型在并行竞速中被更快模型比下去而取消，非自身故障——计独立部分奖励（高于慢成功 0.3、低于快成功 1.0），不完全惩罚。值为运行时配置项（`[0,1]` 校验，reload 热生效），可按观测效果独立调参。
- `ThompsonStateStore.RecordOutcome(string, double reward, double discountFactor)`：
  - `Alpha = Alpha * discount + reward`
  - `Beta  = Beta  * discount + (1.0 - reward)`
  - reward 与 discountFactor 均 `Math.Clamp` 到合法域（reward `[0,1]`，discount `[0.1,1.0]`）
- 二值兼容重载 `RecordOutcome(string, bool, double)` 委托到 reward 重载（`true→1.0`，`false→0.0`）。
- Start state: `Beta(1, 1)` uniform prior
- 慢成功从旧二值语义的「Beta 惩罚」变为「部分 Alpha + 部分 Beta」；竞速失败从旧「硬失败 0.0」变为「0.5」——行为变化（根治型）。

### Hot-Reload Cleanup

```csharp
// On RouterOptions change (models-config.json reload):
tsStoreForReload.Retain(options.Models.Select(m => m.Name));
// Removes entries for deleted/renamed models, preventing _states unbounded growth
```

### Multi-Dimensional Scoring

```csharp
// Score = sum(weight_i * model.GetEffectiveCapability(dimension_i)) for each dimension
// Tolerance: 0.15 (CapabilityScoreTolerance)
// Sorting: score descending; if |score_diff| <= tolerance, cheaper model wins

// Weight profiles by classification:
//   code-detected:    coding=1.0, reasoning=0.6, language=0.3
//   code-complex:     coding=1.0, reasoning=0.8, language=0.2  (debug/fix/refactor/algorithm → Strong)
//   code-simple:      coding=1.0, reasoning=0.3, language=0.4  (hello world/scaffold/example → Medium)
//   math-detected:    reasoning=1.0, coding=0.5, language=0.3
//   complex-instruction: reasoning=0.8, language=0.7
//   translation:      language=1.0, coding=0.1
//   simple-qa:        language=1.0, reasoning=0.1
//   default:          language=0.8, reasoning=0.5
```

### Policy Group Contract & Structured Reason (P2)

- 每个策略声明 `PolicyGroup`（Filter/Classify/Order/Constraint），`RouterEngine.Decide` 按组依赖序执行（Filter→Classify→Order→Constraint），**组内保留串行**（叠加过滤/fallback/重排语义）。
- **为什么不并行**：策略链本质串行——Failover 有 fallback 副作用（从 `AllModels` 补降级）、QuotaAware 既过滤又重排，非纯谓词；genuine 并行需重构独立子链，超出安全范围。分组契约是未来并行化的地基。
- `RouterDecision.ReasonEvents`（`IReadOnlyList<ReasonEvent>`）结构化事件列表，各策略在拼接 `Reason` 字符串之外追加。`Reason` 字符串保持原生成逻辑（测试断言锁定格式）。
- `RouterDecision.ClassificationSignal` / `ClassificationTargetTier`：由 `RuleClassifierPolicy` 填充的结构化分类信号，生产端直接读取（不解析字符串）。`routing_reason` 的 `target=Tier(signal)` 格式保持（`analyze_audit.py` 依赖）。

### Contextual Bandit (LinUCB, P3)

- Gate：`EnableContextualBandit`（默认关）。与 `EnableThompsonSampling` 互斥——同一段内只能由一种重排策略负责，混用会让 `ThompsonStateStore` 与 `ContextualBanditState` 互相覆盖、stat 计数器错位。`RouterOptionsValidator` 启动期强制拒绝两者同时开启（错误信息：`"EnableContextualBandit 与 EnableThompsonSampling 互斥，不能同时开启。"`）。生产路径等价互斥；`LatencyAwarePolicy.ReorderSegment` 内 `bandit > thompson > latency` 优先级顺序仅是防御性兜底（防配置漂移），正常配置下 bandit 与 thompson 不会同时为 true。
- 特征：`ContextualBanditFeatureBuilder` 把分类信号（7 one-hot）+ tier（3 one-hot）+ bias 映射为 11 维向量。
- 打分：`score = θ·x + α·sqrt(xᵀA⁻¹x)`（`ContextualBanditState.Predict`）。
- 更新：`A += x·xᵀ`，`b += reward·x`，θ = A⁻¹·b，历史按 `ContextualBanditDiscountFactor` 衰减。
- 奖励：复用 Thompson reward 语义（快成功 1.0/慢成功 0.3/失败 0/竞速 0.5），`RecordThompsonOutcome` / `RecordThompsonRaceCancelled` 在启用 bandit 时同步更新（需传 `classificationSignal`）。
- 修非上下文 Thompson 「只优化延迟、系统性低估 Strong」缺陷（研究实证 gpt-4o regret 0.447）——LinUCB 用请求特征学习「模型↔任务」匹配。
- 冷启动：θ=0 仅 UCB 项（探索）；热重载 `Retain` 清理已删模型。
- 测试：`ContextualBanditTests`（特征构造、状态数学、上下文影响选型、默认关向后兼容）。

### Fusion Router Improvements (P1-P3)

Fusion Router 的三个改进均**默认关/沿用旧值**，向后兼容。落地于 `08-10-fusion-p1p3`（研究报告 `08-10-fusion-router-algo-research`）。

#### P1 — `FusionRouterPanelTemperature`（可配置 panel 温度多样性）

- 字段：`RoutingOptions.FusionRouterPanelTemperature`（`double?`，默认 `null`）。
- Panel 温度解析：`request.Temperature ?? routing.FusionRouterPanelTemperature ?? routing.FusionRouterTemperature`。
- **analyst 温度不受影响**：analyst 始终用 `routing.FusionRouterTemperature`（低温度保 JSON 稳定）。
- 语义：panel 用于发散采样（建议 >0 引入多样性，对齐 Self-Consistency 的温度多样性收益）；analyst/outer 用于收敛稳定。
- 向后兼容：`PanelTemperature=null` 时 panel 沿用 `FusionRouterTemperature`，行为等同未配置 P1。
- 校验：非 null 时须在 `[0, 2]`（`RouterOptionsValidator`）。

#### P2 — Analyst 解析加固（`response_format` 重试 + 软降级）

- 触发条件：`FusionSynthesis.ParseAnalysis` 在首次 `BuildAnalystRequest` 响应上返回 `null`（JSON 损坏/围栏剥离后仍不可解析）。
- 第一级重试：用 `BuildAnalystRequest(..., requestJsonFormat: true)` 构造带 `response_format={type:"json_object"}` 的请求重试一次（经 `ChatRequest.ExtensionData` 透传，上游不支持时静默忽略，行为回退为普通输出）。
- 第二级软降级：重试仍解析失败且 `ResponseConfidenceChecker.ExtractAssistantText` 拿到非空文本时，调用 `FusionSynthesis.BuildFallbackAnalysis(rawText)` 构造 `FusionAnalysis { Recommendation = rawText, ...空字段 }`，**不**回退串行，保留已付 panel 成本。outer 仍能读 `Recommendation` 写答案。
- 失败边界：
  - **上游失败**（异常，非解析失败）：直接回退串行（行为不变）。
  - **重试也上游失败**：回退串行。
  - **重试响应为空**：回退串行（`502 analyst parse failed (empty retry)`）。
- 审计：重试请求记一条 `fusion_role="analyst"` 审计（reason 标注 `analyst retry(parse)`）；软降级记 `LogWarning("analyst parse failed, degraded to raw text recommendation")`。
- 向后兼容：`FusionRouterAnalystPrompt` 自定义时行为不变（除非解析失败才走新路径）。

#### P3 — `FusionRouterMinComplexity`（融合成本质量门控）

- 字段：`RoutingOptions.FusionRouterMinComplexity`（`RequestComplexity`，默认 `Unknown`）。
- 触发条件追加：`ProxyOrchestrator.cs:128` 在原有 `EnableFusionRouter && !fusionRouterAttempted && failedInThisRequest.Count==0 && !request.Stream && decision.Candidates.Count>=2` 基础上追加 `&& decision.RequestComplexity >= options.Routing.FusionRouterMinComplexity`。
- 枚举序：`Unknown=0 < Simple=1 < Standard=2 < Complex=3`。`>=` 比较天然满足：
  - 默认 `Unknown` 门控：所有复杂度（含 `Unknown`）满足 → 等同旧行为（向后兼容红线——RuleClassifier 关闭时复杂度为 `Unknown` 也放行）。
  - `MinComplexity=Standard`：`Simple` / `Unknown` 请求跳过融合；`Standard` / `Complex` 触发。
- 与 `EnableDynamicFusionPanelSize` 正交：前者 gate 是否融合，后者定 panel 数。两者都读 `RequestComplexity`，不冲突。
- 校验：合法 `RequestComplexity` 枚举值（`Enum.IsDefined`，`RouterOptionsValidator`）。
- 默认行为：所有 `FusionRouterTests` 应保持全绿（旧行为未被打破）。

### Policy Chain Order

| Position | Policy | Gate | Effect |
|----------|--------|------|--------|
| 1 | `CapabilityFilterPolicy` | `EnableCapabilityFilter` | Exclude models lacking vision/tool-use/json-mode tags |
| 2 | `RuleClassifierPolicy` | `EnableRuleClassifier` | Classify request → tier filter; or reorder by multi-dimensional capability scores. Code requests sub-classified by intent (detected on the last user message with fenced code blocks stripped): complex (debug/fix/refactor/algorithm) → Strong, simple code-gen (hello world/scaffold/example) → Medium, no intent / explain → Strong |
| 3 | `SessionAffinityPolicy` | — | Pin session to previously routed model |
| 4 | `SemanticRouterPolicy` | `EnableSemanticRouter` | Override tier by cosine similarity to semantic route phrases |
| 5 | `LongInputPolicy` | `EnableTokenEstimator` | Exclude models with insufficient context window |
| 6 | `LatencyAwarePolicy` | `EnableLatencyAware` / `EnableThompsonSampling` | Reorder within tier by latency or Thompson Beta sampling |
| 7 | `PromptCacheAffinityPolicy` | `EnablePromptCacheAffinity` | Softly promote a successful model for the same privacy-safe stable-prefix SHA-256 |
| 8 | `BudgetGuardPolicy` | `EnableBudgetGuard` | Degrade to Cheap on budget exhaustion; or reject |
| 9 | `QuotaAwarePolicy` | `EnableQuotaAwareRouting` | Exclude known active exhaustion; demote insufficient request/token headroom |
| 10 | `FailoverPolicy` | `EnableFailover` | Exclude circuit-broken models |
| 11 | `LoadBalancePolicy` | — | Round-robin across remaining candidates |

---

## 4. Validation & Error Matrix

| Condition | Error / Behavior | Source |
|-----------|-----------------|--------|
| `Models` empty or null | `ValidateOptionsResult.Fail("Models 不能为空...")` | `RouterOptionsValidator` |
| Model `Name` whitespace/duplicate | Validation fail per model | `RouterOptionsValidator` |
| `LongInputThresholdTokens <= 0` | Validation fail | `RouterOptionsValidator` |
| `EnableThompsonSampling` + `ThompsonLatencyTargetMs <= 0` | Validation fail | `RouterOptionsValidator` |
| `EnableThompsonSampling` + `ThompsonDiscountFactor` outside `[0.5, 0.99]` | Validation fail | `RouterOptionsValidator` |
| `EnableContextualBandit` + `ContextualBanditAlpha <= 0` | Validation fail | `RouterOptionsValidator` |
| `EnableContextualBandit` + `ContextualBanditDiscountFactor` outside `[0.5, 0.99]` | Validation fail | `RouterOptionsValidator` |
| `EnableContextualBandit` + `EnableThompsonSampling` 同开 | Validation fail (启动期互斥拒绝) | `RouterOptionsValidator` |
| `AuditRetentionHours < 1` | Validation fail | `RouterOptionsValidator` |
| `FusionRouterTemperature` outside `[0, 2]` | Validation fail | `RouterOptionsValidator` |
| `FusionRouterPanelTemperature` 非 null 且 outside `[0, 2]` | Validation fail | `RouterOptionsValidator` |
| `FusionRouterMinComplexity` 非合法枚举值 | Validation fail | `RouterOptionsValidator` |
| `FusionRouterPanelTimeoutSeconds < 0` | Validation fail | `RouterOptionsValidator` |
| `FusionRouterMinPanelSize` outside `[2, 5]` or above max panel size | Validation fail | `RouterOptionsValidator` |
| `PromptCacheAffinityTtlSeconds <= 0` | Validation fail | `RouterOptionsValidator` |
| Cached/cache-write price below zero | Validation fail per model | `RouterOptionsValidator` |
| `MaxResponseStreamBytes <= 0` | Validation fail | `RouterOptionsValidator` |
| Unknown `Tags` value | Warning only (not blocking) | `RouterOptionsValidator` |
| No candidate model satisfies capability requirements | Warning + keep original candidates (not empty) | `CapabilityFilterPolicy` |
| All candidates fail | `AllCandidatesFailedException` | `ProxyOrchestrator` |
| Budget exhausted + `EnforceOnExhausted == Reject` | `BudgetExhaustedException` → 429 | `BudgetGuardPolicy` |

---

## 5. Good/Base/Bad Cases

### Good: Multi-dimensional routing with capability scores

```csharp
// Model A: coding=0.95, reasoning=0.80, price=0.5/M (Medium；language 未配置 → 回退 0.78)
// Model B: no capabilities (Medium；维度化回退 coding=0.60, reasoning=0.50, language=0.78), price=0.4/M
// Model C: coding=0.90, reasoning=0.50, price=0.05/M (Cheap；language 未配置 → 回退 0.76)
// Request: "write a Python sorting function" → code-detected
// Weights: coding=1.0, reasoning=0.6, language=0.3
// Scores: A=1.0×0.95+0.6×0.80+0.3×0.78=1.664, C=1.0×0.90+0.6×0.50+0.3×0.76=1.428, B=1.0×0.60+0.6×0.50+0.3×0.78=1.134
// 桶 floor(/0.15)：A=11, C=9, B=7 → Sort: [A, C, B]（分差 > 0.15，价格不参与）
```

### Base: Multi-dimensional routing with close scores → price wins

```csharp
// Model A: language=0.95, price=0.5/M
// Model B: language=0.93, price=0.05/M
// Request: simple QA → language=1.0 weights
// Scores: A=0.95, B=0.93 (diff=0.02 <= 0.15 tolerance)
// Sort: [B, A] (cheaper wins)
```

### Bad: Thompson Sampling active without discount factor validation

```csharp
// Config: EnableThompsonSampling=true, ThompsonDiscountFactor=0.3
// Validation fails: "ThompsonDiscountFactor 必须在 [0.5, 0.99] 范围内"
// Startup blocked. Fix: set discount to 0.95.
```

### Good/Base/Bad: Code-intent sub-classification (RuleClassifierPolicy)

> 代码请求不再一律 `code→Strong`，按意图细分：复杂（debug/fix/refactor/algorithm）→ Strong `code-complex`；简单生成（hello world/scaffold/example）→ Medium `code-simple`；无明确意图/解释类 → 保守 Strong `code-detected`（代码能力优先，宁过度不低估）。

```csharp
// Good: 复杂代码意图 → Strong
// "修复这个 bug\n```python\ndef f(): return 1/0\n```" → code-complex, Strong, Complex

// Base: 简单代码生成 → Medium
// "一个 hello world 示例\n```python\nprint('hi')\n```" → code-simple, Medium, Standard

// Base: 无明确意图的裸代码块 → 保守 Strong
// "```python\ndef quicksort(arr): return arr\n```" → code-detected, Strong

// Bad: 解释类被误归简单 → 复杂解释任务降级到 Medium（质量劣化）
// "解释一下这段代码\n```python\ndef quicksort(arr): ...\n```" → 必须 Strong，不得降级
```

#### 代码意图检测三大陷阱（本会话踩过，已修复并锁定测试）

> **Warning (Gotcha 1)**: 意图正则只跑在**指令文本**（最后一条 user 消息、剔除 fenced code block），**绝不能**跑在 `ConcatMessages` 全量文本上。代码正文里的注释/字符串/标识符（`// simple`、`print("hello world")`）会泄漏意图词，把复杂代码误降级到 Medium。通过 `ExtractInstructionText` + `StripFencedCodeBlocks`（``` / ~~~）实现。

> **Warning (Gotcha 2)**: 意图正则里的英文裸名词（`example`/`simple`/`basic`）会误配代码标识符（`public class Example {}`、`class BasicAuth`）。用明确动词（`scaffold`/`boilerplate`/`explain`）或中文意图词，不用裸名词。

> **Warning (Gotcha 3)**: `explain`/`解释` **不是**简单意图——解释复杂代码需要 Strong 推理。删掉 `explain`/`解释`/`这段代码.*含义` 的 simple 归类，落到保守 Strong。复杂>简单>默认 Strong 的判定顺序保证 complex 信号不降级。

---

## 6. Tests Required

### Test Infrastructure Patterns

| Pattern | Tool | Usage |
|---------|------|-------|
| Seeded `Random` for ThompsonSampler | `new Random(42)` | Deterministic Beta sampling in tests |
| `Func<double, double, double>` injection | `sampleBeta` constructor param | Replace production thread-local RNG with seeded delegate |
| `StubLatencyStatsProvider` | Implement `ILatencyStatsProvider` | Return canned stats or null for cold-start scenarios |
| `TestHelpers.BuildOptions` | `(Name, Tier, MaxCtx, Price)[]` | Quick RouterOptions construction |
| `TestHelpers.BuildRequest` | `(Role, Content)[]` | Quick ChatRequest construction |
| `Setup()` helper | `(options, candidates, query)` → `(Context, Decision)` | Common test arrange pattern |

### Key Assertion Points

| Test | What it asserts |
|------|-----------------|
| `ThompsonSampler_SamplesValidValues` | All samples in `(0, 1)` range across skewed alpha/beta ratios |
| `ThompsonSampler_BetaShape_MeanReflectsAlphaBetaRatio` | `Beta(50,1)` mean > 0.90, `Beta(1,50)` mean < 0.10 (3000 samples, seeded RNG) |
| `ThompsonStateStore_UpdatesParametersWithDiscount` | Exact Alpha/Beta values after sequential good/bad outcomes |
| `ThompsonStateStore_RecordOutcome_ClampsDiscountFactor` | Out-of-range factor clamped to `[0.1, 1.0]` without exceptions |
| `LatencyAwarePolicy_WithThompsonSampling_ReordersCorrectly` | m-good (100 successes) ranks before m-bad (100 failures) with seeded RNG |
| `MultiDimensionalRouting_CalculatesMatchScoreAndSortsCorrectly` | Models sorted by multi-dimensional score, `Reason` contains dimension names |
| `Apply_ComplexCodeIntent_SelectsStrongTier` | debug/fix/refactor/algorithm code → `code-complex`, Strong, Complex |
| `Apply_SimpleCodeIntent_SelectsMediumTier` | hello world/example/scaffold code → `code-simple`, Medium, Standard |
| `Apply_BareCodeBlockNoIntent_KeepsStrongTier` | bare code block (no intent) → `code-detected`, Strong (code capability priority) |
| `Apply_ExplainCode_NotDowngradedToSimple` | `explain`/`解释` code → Strong (explain ≠ simple; needs Strong reasoning) |
| `Apply_CodeBlockContainingHelloWorldString_NotDowngradedToSimple` | intent signals inside code block body → Strong (detection scoped to instruction text) |
| `Apply_CodeClassNamedExample_NotDowngradedToSimple` | `public class Example {}` not downgraded (FP guard) |

### Thompson Sampling test pattern

```csharp
// Arrange: seed RNG for deterministic Beta sampling
var seededRng = new Random(42);
var policy = new LatencyAwarePolicy(
    new StubLatencyStatsProvider(),
    thompsonStore,
    (a, b) => ThompsonSampler.SampleBeta(a, b, seededRng));

// Act
var result = policy.Apply(ctx, initial);

// Assert: m-good (high alpha) should rank first
Assert.Equal("m-good", result.Candidates[0].Name);
Assert.Contains("[Thompson Sampling]", result.Reason);
```

---

## 7. Wrong vs Correct

### Wrong: Multi-dimensional routing without tolerance → always picks Strong

```csharp
// Sort: strictly by score descending
scored.Sort((a, b) => b.Score.CompareTo(a.Score));
// Result: Strong model with 0.9 language score always beats Cheap with 0.85
// → cost optimization defeated, all traffic goes to expensive models
```

### Correct: Multi-dimensional routing with tolerance + price tiebreaker

```csharp
scored.Sort((a, b) =>
{
    double diff = b.Score - a.Score;
    if (Math.Abs(diff) > CapabilityScoreTolerance)
        return diff.CompareTo(0);
    return a.Model.InputPricePerMillion.CompareTo(b.Model.InputPricePerMillion);
});
// Result: cheap model picked when capability difference is negligible
```

### Wrong: Preventing all non-quota failure recording in Thompson state

```csharp
// Do NOT record non-429 failures into Thompson state (supposedly redundant with circuit breaker):
// (skip RecordThompsonOutcome in catch blocks)
// Result: Beta never accumulates for failing models, Thompson sampling has no
// signal to deprioritize them. Circuit breaker (seconds-timescale) and Thompson
// (hours-timescale with discount factor) are complementary, not redundant.
```

### Correct: Record successes and non-429 failures; keep quota separate

```csharp
// Recorded on success (fast→Alpha, slow→Beta):
RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds < options.Routing.ThompsonLatencyTargetMs);

// Recorded on non-429 failure/timeout (Beta += 1):
if (!UpstreamFailureClassifier.IsQuotaLimited(error))
    RecordThompsonOutcome(candidate.Name, false);
```

> **Warning**: Thompson state and circuit breaker (`ModelHealthTracker`) serve complementary timescales for availability failures — circuit breaker excludes failed models for seconds/minutes, while Thompson Beta accumulates a discounted long-term signal. Both record non-429 failures. HTTP 429 belongs only to quota state and must not poison either availability signal.

### Wrong: CapabilityFilter returns empty candidate list on no-match

```csharp
if (filtered.Count == 0) {
    return previous with { Candidates = new List<ModelEndpointOptions>() };
}
// Result: RouterEngine crashes with IndexOutOfRange on Primary access
```

### Correct: CapabilityFilter keeps original candidates with warning

```csharp
if (filtered.Count == 0) {
    return previous with { Reason = $"{previous.Reason}; capability-filter: no candidate has ..." };
    // Candidates unchanged, upstream AI model will return capability error
}
```

### Wrong: Code-intent regex runs on full text including code body

```csharp
return ClassifyCodeIntent(ConcatMessages(request));
// Code body leaks intent words: a comment "// simple" or string "hello world"
// → wrongly downgrades a complex code request to Medium
```

### Correct: Code-intent regex runs on instruction text only (code blocks stripped)

```csharp
return ClassifyCodeIntent(ExtractInstructionText(request));
// ExtractInstructionText = last non-empty user message, fenced blocks (```/~~~) stripped.
// Intent signals come from the user's natural-language instruction, not the code body.
```

---

## Scenario: Provider-aware routing foundation

### 1. Scope / Trigger

- Applies when adding or changing upstream metadata capture, prompt-cache-aware cost accounting, quota-aware routing, stable-prefix affinity, or Fusion panel selection.
- Candidate-order-changing features are opt-in and default off. Metadata capture, normalization, audit persistence, and dashboard projection remain active so operators can evaluate a feature before enabling it.
- Routing policies are synchronous and memory-only. HTTP/header parsing belongs in `OpenAICompatibleModelClient`; persistence belongs in audit stores; policies must never perform network or database I/O.

### 2. Signatures

```csharp
public sealed record UpstreamResponseMetadata
{
    public long? RequestsRemaining { get; init; }
    public long? TokensRemaining { get; init; }
    public DateTimeOffset? RequestsResetAt { get; init; }
    public DateTimeOffset? TokensResetAt { get; init; }
    public DateTimeOffset? RetryAfterAt { get; init; }
    public long? ResponseHeaderLatencyMs { get; init; }
    public long? TimeToFirstTokenMs { get; init; }
}

public sealed record ChatUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int CachedInputTokens { get; init; }
    public int CacheWriteInputTokens { get; init; }
    public int UncachedInputTokens { get; init; }
}

public static string? StablePromptFingerprint.Compute(ChatRequest request);
public void PromptCacheAffinityStore.Record(string fingerprint, string modelName, TimeSpan ttl);
public bool PromptCacheAffinityStore.TryGetModel(string fingerprint, out string? modelName);
public void UpstreamQuotaStateStore.Record(
    string modelName, UpstreamResponseMetadata? metadata, bool rateLimited);
public FusionPanelSelection FusionPanelSelector.Select(
    RouterDecision decision, RoutingOptions options);
```

`ModelEndpointOptions` adds `Provider`, `Family`, nullable `CachedInputPricePerMillion`, and nullable `CacheWriteInputPricePerMillion`. A null cache price falls back to the ordinary input price. `RouterDecision.RequestComplexity` is the only behavioral complexity signal; consumers must not parse `Reason`.

### 3. Contracts

- Normalize only known rate-limit fields. Never retain or log raw headers, API keys, response bodies, prompts, or session content as routing state.
- Cache usage must be nonnegative and internally consistent: `cached + cacheWrite + uncached == prompt`. Malformed, oversized, or contradictory provider fields are clamped to the safe prompt remainder.
- Cache-aware input cost is `(cached * cachedPrice + cacheWrite * cacheWritePrice + uncached * inputPrice) / 1_000_000`; output pricing is unchanged.
- Stable-prefix material is canonical JSON containing system messages plus `functions`, `parallel_tool_calls`, `response_format`, `tool_choice`, and `tools`. The store accepts only 64-character SHA-256 hexadecimal keys, stores only fingerprint/model/TTL metadata, is bounded, and overwrites the affinity with the latest successful downstream model.
- Active session affinity takes precedence only when `EnableSessionAffinity=true` and a session id exists. Merely carrying a session id must not disable prompt-cache affinity.
- Quota state is process-local and hot-reload-pruned. A known future reset permits exclusion while exhausted; insufficient token/request headroom is a soft demotion. Unknown/malformed metadata preserves the original order.
- HTTP 429 updates quota state but is not a model-health or Thompson failure. Non-429 upstream failures continue to update health and Thompson state. This applies equally to serial, streaming, Race, Fusion, Cascade, and health-probe paths.
- Dynamic Fusion panel size maps `Simple` to min, `Standard` to `min + 1` capped at max, and `Complex`/`Unknown` to max. Diversity is soft, preserves the primary model, scores only provider/family dimensions known on the primary, and preserves original order on ties or incomplete metadata.

### 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| Missing or malformed rate-limit/cache usage field | Ignore field; keep safe defaults and candidate order |
| Cache count exceeds prompt count | Clamp components to prompt remainder before costing/auditing |
| Affinity input is not a SHA-256 hex digest | `ArgumentException`; never store arbitrary prompt material |
| Affinity TTL is non-positive | Startup validation failure or `ArgumentOutOfRangeException` at store boundary |
| Quota snapshot has active exhaustion reset | Exclude candidate until reset |
| Quota snapshot lacks enough estimated token headroom | Demote candidate after viable candidates |
| All quota candidates are exhausted | Empty candidate list is allowed to flow to the established all-candidates failure path |
| Upstream status is 429 | `QuotaLimited=true`; quota update only; no circuit/Thompson penalty |
| Upstream status is 5xx/network/timeout | Health and Thompson failure; normal failover behavior |
| Fusion min outside `[2,5]` or above max | Startup validation failure |
| Provider/family missing on primary | No diversity bonus for that dimension |

### 5. Good/Base/Bad Cases

- **Good**: A repeated system/tool prefix succeeds on model B; the SHA-256 affinity promotes B on the next eligible request, and cached tokens use B's discounted cache price.
- **Base**: No cache/rate headers are returned; usage falls back to ordinary prompt pricing and every opt-in policy preserves the existing candidate order.
- **Bad**: A 429 is sent to `ModelHealthTracker.RecordFailure`; the circuit opens for a healthy but temporarily quota-limited endpoint and double-punishes it. Record quota exhaustion only.

### 6. Tests Required

- Provider-specific cache usage parsing, malformed numeric fields, contradictory totals, missing usage, response-header latency, and streaming first-data TTFT.
- Cache price fallback and split-token cost math.
- SHA-256 canonicalization, stable-field selection, TTL/size eviction, invalid fingerprint rejection, downstream overwrite, session precedence, and failed-model filtering.
- Quota reset formats, 429 without headers, insufficient headroom, reset expiry, hot-reload pruning, and unchanged-order fallback.
- Serial/streaming/Race/Fusion/Cascade/probe regression matrix proving 429 is quota-only while 5xx still records health/Thompson failure.
- Dynamic panel sizes for all `RequestComplexity` values; primary preservation, provider/family diversity, tie stability, and incomplete metadata.
- Configuration binding/hot reload and management API/dashboard round trips for all new model metadata and prices.

### 7. Wrong vs Correct

```csharp
// Wrong: behavior depends on diagnostic prose and raw prompt storage.
if (decision.Reason.Contains("simple")) panelSize = 2;
affinity[prompt] = model.Name;

// Correct: typed behavior plus privacy-safe stable material.
panelSize = decision.RequestComplexity == RequestComplexity.Simple ? min : max;
string? fingerprint = StablePromptFingerprint.Compute(request);
if (fingerprint is not null) affinity.Record(fingerprint, model.Name, ttl);
```

```csharp
// Wrong: all upstream failures poison health state.
health.RecordFailure(model.Name); // includes 429

// Correct: classify once and keep quota separate from availability.
bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(error);
outcomes.RecordQuota(model.Name, metadata, quotaLimited);
if (!quotaLimited) outcomes.RecordFailure(model.Name, error);
```

## Scenario: Fusion panel-level timeout

### 1. Scope / Trigger

- Applies when changing `FusionRouter` panel orchestration, panel cancellation, or the `FusionRouterPanelTimeoutSeconds` config.
- Trigger: `EnableFusionRouter=true` and `FusionRouterPanelTimeoutSeconds > 0`. Default `0` preserves the legacy `Task.WhenAll` behavior (wait for all panels) — backward-compatible.

### 2. Signatures

```csharp
// Config (RoutingOptions):
public int FusionRouterPanelTimeoutSeconds { get; set; } = 0; // 0 = disabled

// FusionRouter.ExecuteAsync creates one independent linked CTS per admitted panel:
CancellationTokenSource panelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
if (panelTimeoutMs > 0) panelCts.CancelAfter(panelTimeoutMs);
// Passed to client.CompleteRawAsync(panelRequest, panelCts.Token)
```

### 3. Contracts

- Each panel gets its **own** linked CTS. One panel timing out must not cancel siblings.
- Panel timeout fires `OperationCanceledException` inside `InvokePanelAsync`, caught by its existing `catch (Exception ex)` and returned as `(model, null, ex, elapsedMs)` — no exception escapes the task.
- After `Task.WhenAll(panelTasks)` returns, **all** panel CTS are Disposed in a `finally` block (CTS.Dispose is idempotent; safe across normal and external-cancel paths).
- Timed-out panel is treated as a **failure**: added to `failedInThisRequest`, recorded via `RecordFailure` (circuit breaker) + `RecordThompsonOutcome(false)`, audited with `lastErrorMessage="panel-timeout"`, status `408`.
- `PickUnfailedFallback` skips timed-out models when selecting analyst/outer — the fallback model will differ from the timed-out panel.
- External `ct` cancellation remains the highest-priority cancel: it throws out of `WhenAll`, releases probe slots, and does **not** record failure/Thompson/cost/audit (unchanged pre-timeout behavior).

### 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| `FusionRouterPanelTimeoutSeconds == 0` | Panel-level timeout disabled; `Task.WhenAll` waits for all panels (legacy) |
| `FusionRouterPanelTimeoutSeconds > 0` + 1 slow panel | Slow panel cancels at threshold, recorded as failure (408); remaining panels proceed to analyst |
| All panels time out | `panelAnswers.Count == 0` → fall back to serial (existing path) |
| External `ct` cancels during panel wait | Release probe slots, no failure recorded, propagate cancel |
| `FusionRouterPanelTimeoutSeconds < 0` | Startup validation fail |

### 5. Good/Base/Bad Cases

- **Good**: 3 panels configured, timeout=1s. Model A sleeps 10s (cancelled at 1s), B/C return instantly. Analyst runs on B (fallback skips A), outer returns 200 with final answer.
- **Base**: timeout=0 (disabled). One panel sleeps 200ms; `WhenAll` waits for it, all panels succeed, 200 returned. Proves backward compatibility.
- **Bad**: timeout=1s but all panels sleep 10s and serial path has no timeout guard → serial calls hang until external ct. Mitigation: serial-path mocks must fail fast on 2nd call; production relies on per-model HTTP timeout.

### 6. Tests Required

| Test | Assertion points |
|------|------------------|
| `FusionRouter_PanelTimeout_SlowPanelDoesNotBlockAnalyst` | HTTP 200; response contains final answer; `panel=2, analyst≠timed-out-model` in logs |
| `FusionRouter_PanelTimeout_AllTimeoutFallsBackToSerial` | HTTP ≠ 200; test completes within 15s safety window (proves timeout fired, no deadlock) |
| `FusionRouter_PanelTimeout_ZeroKeepsBackwardCompatible` | HTTP 200 with timeout=0; slow (200ms) panel is waited for, not cancelled |

Test setup notes:
- Slow panel mock: `async (req, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); ... }` — the `ct` here is the panel's linked token, cancelled at timeout.
- All-timeout test must make serial-path calls (2nd+) fail fast, otherwise serial path hangs on `Task.Delay` (panel timeout does not protect serial path — by design).
- Wrap each test in `CancellationTokenSource(TimeSpan.FromSeconds(15))` as a deadlock-regression guard.

### 7. Wrong vs Correct

```csharp
// Wrong: one shared CTS for all panels — first timeout cancels everyone.
var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
sharedCts.CancelAfter(panelTimeoutMs);
foreach (var model in admitted)
    panelTasks.Add(InvokePanelAsync(model, sharedCts.Token));
// Result: one slow panel cancels fast siblings, defeating parallel panel diversity.
```

```csharp
// Correct: one independent linked CTS per panel.
foreach (var model in admitted)
{
    var panelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (panelTimeoutMs > 0) panelCts.CancelAfter(panelTimeoutMs);
    panelCtsList.Add(panelCts);
    panelTasks.Add(InvokePanelAsync(model, panelCts.Token));
}
// Collected in panelCtsList, Disposed in the WhenAll finally block.
```

```csharp
// Wrong: treat any OperationCanceledException as external cancel.
catch (OperationCanceledException) { _healthTracker.ReleaseProbe(model.Name); throw; }
// Result: panel timeouts are silently swallowed — slow models never get circuit-broken,
// and WhenAll never sees the timeout because the exception escaped the task.
```

```csharp
// Correct: distinguish panel timeout from external cancel.
// In InvokePanelAsync, catch external ct cancel and rethrow; everything else (incl. panel
// timeout) is caught by catch (Exception ex) and returned as a failure tuple.
// In the result loop, identify timeout:
bool panelTimedOut = error is OperationCanceledException && !ct.IsCancellationRequested;
// → status 408, reason "panel timeout", RecordFailure + Thompson(false)
```

> **Warning**: Panel timeout protects **only the Fusion panel segment**. Serial fallback path (`ProxyOrchestrator.SendAsync` candidate loop) has no panel-level timeout — it relies on per-model HTTP timeout (`TimeoutSeconds` on `ModelEndpointOptions`). Tests that simulate slow serial-path models must fail fast on the 2nd call, or the test will hang.

## Design Decisions

### Decision: Independent gates for Thompson Sampling and Latency-Aware

**Context**: Original implementation had Thompson Sampling implicitly gated by `EnableLatencyAware`. Users wanting adaptive exploration without latency stats had to enable both.

**Decision**: `EnableThompsonSampling` and `EnableLatencyAware` are independent gates. Either can be true alone. Both false → policy skipped. Both true → `ReorderSegment` checks Thompson first.

### Decision: Tier segmentation for reordering

**Context**: Without tier segmentation, latency-aware reordering could promote a Cheap model ahead of a Strong one, violating the tier contract.

**Decision**: `LatencyAwarePolicy` segments candidates by tier, reorders only within each segment. Cross-tier order preserved.

### Decision: `CapabilityScoreTolerance = 0.15` for price tiebreaker

**Context**: Without tolerance, the Strong tier fallback (0.9) would always beat Cheap (0.3) on every dimension, making multi-dimensional routing degenerate to tier-only sorting.

**Decision**: When capability score difference <= 0.15, cheaper model wins. This allows cheap models with sufficient capability to serve requests cost-effectively.

### Decision: Per-panel independent CTS for Fusion panel timeout

**Context**: `FusionRouter` used `Task.WhenAll(panelTasks)` with only the global request `ct` as cancellation. A single slow panel (e.g., 30s stall) blocked the analyst stage even when other panels finished in 1s. Considered a shared panel-timeout CTS, or a `WhenAny`-style early-exit.

**Options Considered**:
1. Shared CTS for all panels — first timeout cancels all siblings.
2. `WhenAny`-style: advance to analyst as soon as ≥1 panel succeeds — rejected because analyst needs **all** panel answers for consensus/contradiction analysis; partial input degrades analysis quality.
3. Per-panel independent CTS with `CancelAfter` (chosen).

**Decision**: One independent `CancellationTokenSource.CreateLinkedTokenSource(ct)` per panel, each with its own `CancelAfter(panelTimeoutMs)`. A timed-out panel is recorded as a failure (408, `RecordFailure`, Thompson penalty) and excluded from analyst/outer fallback selection via `PickUnfailedFallback`. `Task.WhenAll` is preserved so analyst still waits for all non-timed-out panels.

**Default off**: `FusionRouterPanelTimeoutSeconds = 0` keeps the legacy wait-for-all behavior, making the feature opt-in and backward-compatible.

**Extensibility**: A future analyst/outer-level timeout could reuse the same per-stage CTS pattern if those stages exhibit similar tail-latency problems.

### Decision: `EnableContextualBandit` × `EnableThompsonSampling` startup-time mutex

**Context**: 24h 审查发现 `RouterOptionsValidator` 接受两者同时开启，`LatencyAwarePolicy.ReorderSegment` 静默 cascade bandit 优先 Thompson——契约与 spec `3. Contracts` 中「互斥」措辞错位，运维错误配置无启动期信号，stat 计数器会被两类状态互相污染。

**Options Considered**:
1. 删 spec「互斥」措辞，改成「cascade with bandit priority」——允许同开。
2. 启动期 validator 拒绝同开（chosen）——fail fast，明确边界。
3. 仅文档警告，不强制——重蹈覆辙。

**Decision**: `RouterOptionsValidator` 启动期拒绝 `EnableContextualBandit=true && EnableThompsonSampling=true`，错误信息：「`EnableContextualBandit 与 EnableThompsonSampling 互斥，不能同时开启。LinUCB 在启用时段内替代 Thompson，请只开启其中一个。`」。`LatencyAwarePolicy` 内 `bandit > thompson > latency` 优先级顺序保留作为防御性兜底（防配置漂移），但生产配置下 bandit 与 thompson 不会同时为 true。测试：`RouterOptionsValidatorTests.BanditAndThompsonBothEnabled_ShouldReturnFailure` 证明互斥被拒绝；`BanditAndThompsonNotBothEnabled_ShouldSucceed`（Theory：TT/FF/FT）保证三档合法配置不踩。

**Default off**: 两项默认均 false，互斥规则对默认配置无影响（向后兼容）。

**Extensibility**: 若未来需要在 bandit 内嵌套 thompson-style 后验，可重构成"单 bandit gate + 内部分支"，届时此 mutex 规则可放宽。

## Scenario: Dashboard Policy Hot-Tuning, Circuit Breaker Overrides & Tenant Client Keys

### 1. Scope / Trigger
- Hot-tuning system routing policies and daily budget directly from the UI control studio without container restart.
- Manual emergency override of model circuit breaker states (`Closed`, `Open`, `HalfOpen`) for operations & isolation.
- Issuing and enforcing multi-tenant client API Access Keys with individual daily budget ($USD) and QPS rate limits.

### 2. Signatures
- `GET /api/dashboard/config` -> `Results.Ok(SystemConfigDto)`
- `PUT /api/dashboard/config` (`UpdateSystemConfigRequest`) -> `Results.Ok`
- `POST /api/dashboard/circuits/{name}/override` (`CircuitOverrideRequest`) -> `Results.Ok`
- `GET /api/dashboard/keys`, `POST /api/dashboard/keys`, `PUT /api/dashboard/keys/{key}`, `DELETE /api/dashboard/keys/{key}` -> `ClientKeyService` CRUD endpoints
- `ModelHealthTracker.ForceSetState(string modelName, CircuitState newState)`
- `ClientKeyService.CreateKey(string tenantName, decimal dailyBudgetUsd, int maxQps)`

### 3. Contracts
- `UpdateSystemConfigRequest`: 9 policy toggles (`EnableFailover`, `EnableBudgetGuard`, `EnableRuleClassifier`, `EnableLatencyAware`, `EnableSemanticRouter`, `EnablePiiAnonymization`, `EnableDataSovereignty`, `EnableJsonAstAutoRepair`, `EnableFusionRouter`), `DailyBudgetUsd`, `EnforceOnExhausted`.
- `CircuitOverrideRequest`: `TargetState` ("Closed", "Open", "HalfOpen").
- `ClientKeyInfo`: `Key` ("opti-key-..."), `TenantName`, `DailyBudgetUsd`, `DailySpendUsd`, `MaxQps`, `Enabled`, `CreatedAt`.

### 4. Validation & Error Matrix
- `TargetState` not valid Enum -> `400 Bad Request` ("Invalid target state...")
- `TenantName` empty -> `400 Bad Request` ("TenantName is required.")
- `Key` not found -> `404 Not Found` ("Client key '{key}' not found.")

### 5. Good/Base/Bad Cases
- Good: `POST /api/dashboard/circuits/gpt-4o/override` with `{"targetState": "Closed"}` -> Resets circuit state to Closed, clears failure count.
- Base: `PUT /api/dashboard/config` with `{"enableLatencyAware": true}` -> Instant hot reload in memory for next `RouterEngine.Decide()` run.
- Bad: `POST /api/dashboard/circuits/unknown/override` with `{"targetState": "Invalid"}` -> Returns 400 Bad Request.

### 6. Tests Required
- `ModelHealthTracker.ForceSetState` test: verifies transition from Open to Closed clears failure count and cooldown time.
- `ClientKeyService` persistence test: verifies CRUD operations persist safely across reloads.
- Dashboard endpoints integration test: verifies HTTP status codes and payloads.

### 7. Wrong vs Correct
#### Wrong
```csharp
// Direct mutation of circuit state without lock protection
circuit.State = CircuitState.Closed; // Thread-unsafe! Race conditions under concurrent routing decisions!
```

#### Correct
```csharp
// Thread-safe state force override through ModelHealthTracker
tracker.ForceSetState(modelName, CircuitState.Closed);
```
