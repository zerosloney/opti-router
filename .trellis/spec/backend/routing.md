# Routing

> Multi-model routing engine, policy chain, multi-dimensional capability routing, and Thompson Sampling MAB.

---

## 1. Scope / Trigger

- Routing engine (`RouterEngine`) orchestrates a chain of `IRouterPolicy` policies to produce a ranked candidate list.
- Policy chain order (defined in `Program.cs`): CapabilityFilter → RuleClassifier → SessionAffinity → SemanticRouter → LongInput → LatencyAware → BudgetGuard → Failover → LoadBalance.
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

// Fallback by tier when Capabilities dict has no entry:
//   Strong -> 0.9, Medium -> 0.6, Cheap -> 0.3
```

### Latency Stats

```csharp
public sealed record ModelLatencyStats(double AverageLatencyMs, int SampleCount);

public interface ILatencyStatsProvider
{
    ModelLatencyStats? GetStats(string modelName);
    void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats);
}

// Thread-safe implementation: volatile reference swap, O(1) reads
public sealed class LatencyStatsCache : ILatencyStatsProvider;
```

---

## 3. Contracts

### Config Keys (RoutingOptions)

| Key | Type | Default | Validation |
|-----|------|---------|------------|
| `EnableMultiDimensionalRouting` | bool | `false` | — |
| `EnableThompsonSampling` | bool | `false` | — |
| `ThompsonDiscountFactor` | double | `0.95` | `[0.5, 0.99]` when `EnableThompsonSampling=true` |
| `ThompsonLatencyTargetMs` | double | `800.0` | `> 0` when `EnableThompsonSampling=true` |
| `EnableLatencyAware` | bool | `false` | — |
| `LatencyMinSamples` | int | `10` | — |
| `LatencyStatsWindowMinutes` | int | `60` | — |

### Thompson Outcome Recording

```csharp
// Called in ProxyOrchestrator on every model attempt (success or failure):
// isGood = success AND (elapsedMs < ThompsonLatencyTargetMs)
RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds < options.Routing.ThompsonLatencyTargetMs);
// → calls _tsStore.RecordOutcome(modelName, isGood, routing.ThompsonDiscountFactor)
```

- `isGood == true` (fast success, latency < target): `Alpha = Alpha * discount + 1.0`
- `isGood == false` (slow success, timeout, network/model error, or fusion candidate cancelled): `Beta = Beta * discount + 1.0`
- Start state: `Beta(1, 1)` uniform prior
- `discountFactor` clamped to `[0.1, 1.0]` via `Math.Clamp`

### Hot-Reload Cleanup

```csharp
// On RouterOptions change (models-config.json reload):
tsStoreForReload.Retain(options.Models.Select(m => m.Name));
// Removes entries for deleted/renamed models, preventing _states unbounded growth
```

### Multi-Dimensional Scoring

```csharp
// Score = sum(weight_i * model.GetEffectiveCapability(dimension_i)) for each dimension
// Tolerance: 0.05 (CapabilityScoreTolerance)
// Sorting: score descending; if |score_diff| <= tolerance, cheaper model wins

// Weight profiles by classification:
//   code-detected:    coding=1.0, reasoning=0.6, language=0.3
//   math-detected:    reasoning=1.0, coding=0.5, language=0.3
//   complex-instruction: reasoning=0.8, language=0.7
//   translation:      language=1.0, coding=0.1
//   simple-qa:        language=1.0, reasoning=0.1
//   default:          language=0.8, reasoning=0.5
```

### Policy Chain Order

| Position | Policy | Gate | Effect |
|----------|--------|------|--------|
| 1 | `CapabilityFilterPolicy` | `EnableCapabilityFilter` | Exclude models lacking vision/tool-use/json-mode tags |
| 2 | `RuleClassifierPolicy` | `EnableRuleClassifier` | Classify request → tier filter; or reorder by multi-dimensional capability scores |
| 3 | `SessionAffinityPolicy` | — | Pin session to previously routed model |
| 4 | `SemanticRouterPolicy` | `EnableSemanticRouter` | Override tier by cosine similarity to semantic route phrases |
| 5 | `LongInputPolicy` | `EnableTokenEstimator` | Exclude models with insufficient context window |
| 6 | `LatencyAwarePolicy` | `EnableLatencyAware` / `EnableThompsonSampling` | Reorder within tier by latency or Thompson Beta sampling |
| 7 | `BudgetGuardPolicy` | `EnableBudgetGuard` | Degrade to Cheap on budget exhaustion; or reject |
| 8 | `FailoverPolicy` | `EnableFailover` | Exclude circuit-broken models |
| 9 | `LoadBalancePolicy` | — | Round-robin across remaining candidates |

---

## 4. Validation & Error Matrix

| Condition | Error / Behavior | Source |
|-----------|-----------------|--------|
| `Models` empty or null | `ValidateOptionsResult.Fail("Models 不能为空...")` | `RouterOptionsValidator` |
| Model `Name` whitespace/duplicate | Validation fail per model | `RouterOptionsValidator` |
| `LongInputThresholdTokens <= 0` | Validation fail | `RouterOptionsValidator` |
| `EnableThompsonSampling` + `ThompsonLatencyTargetMs <= 0` | Validation fail | `RouterOptionsValidator` |
| `EnableThompsonSampling` + `ThompsonDiscountFactor` outside `[0.5, 0.99]` | Validation fail | `RouterOptionsValidator` |
| `AuditRetentionHours < 1` | Validation fail | `RouterOptionsValidator` |
| `FusionRouterTemperature` outside `[0, 2]` | Validation fail | `RouterOptionsValidator` |
| `MaxResponseStreamBytes <= 0` | Validation fail | `RouterOptionsValidator` |
| Unknown `Tags` value | Warning only (not blocking) | `RouterOptionsValidator` |
| No candidate model satisfies capability requirements | Warning + keep original candidates (not empty) | `CapabilityFilterPolicy` |
| All candidates fail | `AllCandidatesFailedException` | `ProxyOrchestrator` |
| Budget exhausted + `EnforceOnExhausted == Reject` | `BudgetExhaustedException` → 429 | `BudgetGuardPolicy` |

---

## 5. Good/Base/Bad Cases

### Good: Multi-dimensional routing with capability scores

```csharp
// Model A: coding=0.95, reasoning=0.80, price=0.5/M
// Model B: no capabilities (falls back to Medium=0.6), price=0.4/M
// Model C: coding=0.90, reasoning=0.50, price=0.05/M
// Request: "write a Python sorting function" → code-detected
// Weights: coding=1.0, reasoning=0.6
// Scores: A=1.43, C=1.20, B=0.84
// Sort: [A, C, B] (score gap > 0.05, so price tiebreaker not used)
```

### Base: Multi-dimensional routing with close scores → price wins

```csharp
// Model A: language=0.95, price=0.5/M
// Model B: language=0.93, price=0.05/M
// Request: simple QA → language=1.0 weights
// Scores: A=0.95, B=0.93 (diff=0.02 <= 0.05 tolerance)
// Sort: [B, A] (cheaper wins)
```

### Bad: Thompson Sampling active without discount factor validation

```csharp
// Config: EnableThompsonSampling=true, ThompsonDiscountFactor=0.3
// Validation fails: "ThompsonDiscountFactor 必须在 [0.5, 0.99] 范围内"
// Startup blocked. Fix: set discount to 0.95.
```

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

### Wrong: Preventing failure recording in Thompson state (fear of double-counting)

```csharp
// Do NOT record failures into Thompson state (supposedly redundant with circuit breaker):
// (skip RecordThompsonOutcome in catch blocks)
// Result: Beta never accumulates for failing models, Thompson sampling has no
// signal to deprioritize them. Circuit breaker (seconds-timescale) and Thompson
// (hours-timescale with discount factor) are complementary, not redundant.
```

### Correct: Record Thompson outcome on every attempt (success and failure)

```csharp
// Recorded on success (fast→Alpha, slow→Beta):
RecordThompsonOutcome(candidate.Name, attemptSw.ElapsedMilliseconds < options.Routing.ThompsonLatencyTargetMs);

// Recorded on failure/timeout (Beta += 1):
RecordThompsonOutcome(candidate.Name, false);
```

> **Warning**: Thompson state and circuit breaker (`ModelHealthTracker`) serve complementary timescales — circuit breaker excludes failed models for seconds/minutes (cooldown), while Thompson Beta accumulates over hours (discount-weighted). Both record failures. This is intentional: removing Thompson failure recording would deprive the MAB of long-term signal, making it unable to deprioritize consistently failing models. The circuit breaker handles short-term exclusion; Thompson handles long-term adaptive ranking.

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

---

## Design Decisions

### Decision: Independent gates for Thompson Sampling and Latency-Aware

**Context**: Original implementation had Thompson Sampling implicitly gated by `EnableLatencyAware`. Users wanting adaptive exploration without latency stats had to enable both.

**Decision**: `EnableThompsonSampling` and `EnableLatencyAware` are independent gates. Either can be true alone. Both false → policy skipped. Both true → `ReorderSegment` checks Thompson first.

### Decision: Tier segmentation for reordering

**Context**: Without tier segmentation, latency-aware reordering could promote a Cheap model ahead of a Strong one, violating the tier contract.

**Decision**: `LatencyAwarePolicy` segments candidates by tier, reorders only within each segment. Cross-tier order preserved.

### Decision: `CapabilityScoreTolerance = 0.05` for price tiebreaker

**Context**: Without tolerance, the Strong tier fallback (0.9) would always beat Cheap (0.3) on every dimension, making multi-dimensional routing degenerate to tier-only sorting.

**Decision**: When capability score difference <= 0.05, cheaper model wins. This allows cheap models with sufficient capability to serve requests cost-effectively.