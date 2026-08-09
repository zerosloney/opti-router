# Quality Guidelines

> Code quality standards for backend development.

---

## Forbidden Patterns

| Pattern | Why | Instead |
|---------|-----|---------|
| `string` matching on `RouterDecision.Reason` | Fragile contract; refactoring reason text silently breaks callers | Use dedicated boolean flags (e.g., `BudgetExhausted`) |
| Static/shared `Random` without lock | Thread-safety bugs; non-deterministic failures in multithreaded tests | `ThreadLocal<Random>` (production) or seeded instance (tests) |
| Blocking I/O in routing policy `Apply()` | Policy is called on the request thread; blocking == latency spike | Use memory snapshots (`ILatencyStatsProvider`), zero I/O in policy path |
| Raw `Task.Run()` for CPU-bound work in ASP.NET Core | Steals thread-pool threads from request handling | Use `ThreadPool.QueueUserWorkItem` or dedicated background service |
| `Exception`-based control flow in routing | Costly stack traces; hard to reason about | Use `RouterDecision.BudgetExhausted` flag pattern |

---

## Required Patterns

### Immutable Decision Records

```csharp
// RouterDecision and RouterContext are `record` types.
// Policies return a new record with `with { ... }` — never mutate in place.
return previous with { Candidates = filtered, Reason = $"{...}" };
```

### Policy Injection via Constructor

```csharp
// Policies accept dependencies via constructor, never resolve from service locator.
public LatencyAwarePolicy(
    ILatencyStatsProvider statsProvider,
    ThompsonStateStore tsStore,
    Func<double, double, double>? sampleBeta = null)
{
    _statsProvider = statsProvider ?? throw new ArgumentNullException(nameof(statsProvider));
    // ...
    _sampleBeta = sampleBeta ?? ThompsonSampler.SampleBeta;
}
```

### `intentional-simple` Comment for Known Ceilings

```csharp
// intentional-simple: O(n^2) scan, fine for <50 models.
// intentional-simple: whole-map swap, O(1) read visibility. Models <50, rebuild cost negligible.
```

---

## Testing Requirements

### Deterministic Randomness

```csharp
// ThompsonSampler exposes SampleBeta(alpha, beta, Random rng) overload for test injection.
// LatencyAwarePolicy accepts Func<double, double, double>? sampleBeta delegate.
var seededRng = new Random(42);
var policy = new LatencyAwarePolicy(
    new StubLatencyStatsProvider(),
    thompsonStore,
    (a, b) => ThompsonSampler.SampleBeta(a, b, seededRng));
```

### Stub Providers

```csharp
// For latency tests, implement ILatencyStatsProvider inline:
public sealed class StubLatencyStatsProvider : ILatencyStatsProvider
{
    public ModelLatencyStats? GetStats(string modelName) => null; // cold start
    public void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats) { }
}
```

### Test Helper Pattern

```csharp
// Arrange helper: (RouterOptions, candidates) → (RouterContext, RouterDecision)
var options = TestHelpers.BuildOptions(
    ("m-good", ModelTier.Medium, 8000, 1m),
    ("m-bad", ModelTier.Medium, 8000, 1m));
var (ctx, initial) = Setup(options, options.Models, "hi");
```

### Assertion Style

- Use `Assert.Contains` / `Assert.DoesNotContain` for reason string checks
- Use `Assert.Equal` for exact candidate order
- Prefer `[Theory]` + `[InlineData]` for parameterized validation tests
- Use `[Fact]` for single-scenario behavioral tests

### What to Test

| Layer | Test Coverage |
|-------|--------------|
| Sampler | Range validity, distribution shape (mean), deterministic with seeded RNG |
| State store | Alpha/Beta updates, discount clamping, thread safety, Retain cleanup |
| Policy | Ordering, filtering, edge cases (empty, single candidate, cold start) |
| Config validation | Each field boundary, combination constraints, warning-only paths |
| RouterEngine | Full chain integration, fallback logic, budget exhaustion path |

---

## Code Review Checklist

- [ ] Policy uses `with { ... }` not mutation
- [ ] New config key has validation in `RouterOptionsValidator`
- [ ] New config key documented in `appsettings.example.json` and README
- [ ] `BudgetExhausted` flag used instead of `Reason` string matching
- [ ] No blocking I/O in routing policy `Apply()`
- [ ] Thompson outcome recorded at every success call site (not just primary path)
- [ ] Test uses seeded RNG for non-deterministic algorithms
- [ ] `intentional-simple` comment on known ceilings
- [ ] New `IRouterPolicy` added to policy chain in `Program.cs` in correct order