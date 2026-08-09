# Logging Guidelines

> Structured logging conventions: levels, format, what to log, what never to log.

---

## 1. Scope / Trigger

- All logging uses the ASP.NET Core `ILogger<T>` abstraction (constructor-injected). No custom logging framework.
- Every log call uses **structured message templates** with named placeholders — never string interpolation.
- Log messages are written in **Chinese** (project convention; see README note that *docs* are English but *log strings* are Chinese).
- Background services and the request path log at distinct levels; see the matrix below.

---

## 2. Signatures

### Injection Pattern

```csharp
// Constructor-injected typed logger, always null-guarded.
private readonly ILogger<ProxyOrchestrator> _logger;

public ProxyOrchestrator(
    ...,
    ILogger<ProxyOrchestrator> logger)
{
    ...
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
```

### Test-Friendly Logger Construction

```csharp
// RouterOptionsValidator exposes a null-logger ctor so tests need no logger:
public RouterOptionsValidator() : this(NullLogger<RouterOptionsValidator>.Instance) { }
```

### Message Template Form

```csharp
// Structured: machine-readable named placeholders, never $"..." interpolation.
_logger.LogWarning("Model {Name} failed (status {Status}), trying next candidate{Tripped}",
    candidate.Name, ex.StatusCode, tripped ? " (circuit tripped)" : "");
```

---

## 3. Contracts

### Log Level Usage (from actual code)

| Level | What to log | Example |
|-------|-------------|---------|
| `LogDebug` | High-frequency diagnostic detail, **guarded** by `IsEnabled(LogLevel.Debug)` | Route decision + candidate list, health probe OK, latency stats aggregated, audit evicted count, cascade self-verify failed |
| `LogInformation` | Normal lifecycle/completion events | Request completed (model + cost), circuit skip, cascade upgrade, fusion race result, models-config saved/reloaded |
| `LogWarning` | Recoverable failures, degraded behavior, config hygiene | Model failed/timed out/network error, mid-stream fault, cost-ledger write failed, health probe FAILED, unknown model Tags, production without HTTPS, background aggregation/eviction/gauge failure |
| `LogError` | Unrecoverable but non-fatal operations | models-config.json load failure, config reload failure after external change |
| `LogCritical` | (unused — process-level fatal handling is default) | — |

### Structured Placeholders Used

`{Model}`, `{Name}`, `{Status}`, `{Cost}`, `{Path}`, `{Count}`, `{GroupId}`, `{Tripped}`, `{Cheap}`, `{Strong}`, `{Reason}`, `{Names}`, `{Cutoff:O}`, `{Ms}`, `{Failure}`, `{Unknown}`, `{Known}`, `{ChangeType}`

### Exception-First Overload

```csharp
// Pass the exception as the FIRST argument; the message is the context.
_logger.LogWarning(ex, "Cost ledger write failed; cost {Cost} not recorded", cost);
_logger.LogError(ex, "Failed to load models-config.json, returning empty list");
```

### Debug Guard Pattern

```csharp
// Expensive message building (string.Join over candidates) only when debug enabled.
if (_logger.IsEnabled(LogLevel.Debug))
    _logger.LogDebug("Route decision: {Reason}, candidates=[{Names}]",
        decision.Reason, string.Join(", ", decision.Candidates.Select(c => c.Name)));
```

### Logging Config (appsettings.json)

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning"
  }
}
```

---

## 4. Validation & Error Matrix

| Condition | Log Level | Message intent |
|-----------|-----------|----------------|
| Model returns non-2xx | `LogWarning` | `Model {Name} failed (status {Status}), trying next candidate{Tripped}` |
| Network failure (`HttpRequestException`) | `LogWarning` | `Model {Name} network request failed, trying next candidate{Tripped}` |
| Timeout (`OperationCanceledException` non-cancel) | `LogWarning` | `Model {Name} timed out, trying next{Tripped}` |
| Streaming fault mid-stream | `LogWarning` | `Streaming model {Name} failed mid-stream{Tripped}` |
| Health probe failed | `LogWarning` | `Health probe FAILED: {Name} ({Reason}){Tripped}` |
| Cost-ledger write failure | `LogWarning(ex, ...)` | `Cost ledger write failed; cost {Cost} not recorded` |
| Background service failure | `LogWarning(ex, ...)` | `Latency stats aggregation failed` / `Metrics gauge refresh failed` / `Audit retention eviction failed` |
| Unknown model Tags | `LogWarning` | `模型 {Name} 含未识别的 Tags: {Unknown}。已知标签: {Known}。` |
| Production without HTTPS | `LogWarning` (`app.Logger`) | `Production environment without HTTPS. ProxyApiKey will transit in plaintext...` |
| models-config load failure | `LogError(ex, ...)` | `Failed to load models-config.json, returning empty list` |

---

## 5. Good/Base/Bad Cases

### Good: Structured template with named placeholders

```csharp
_logger.LogInformation("Non-streaming request completed: model={Model}, cost={Cost}",
    candidate.Name, CostCalculator.Compute(response.Usage, candidate).ToString("F6"));
// → "Non-streaming request completed: model=gpt-4o, cost=0.001234"
```

### Base: Debug-guarded expensive message

```csharp
if (_logger.IsEnabled(LogLevel.Debug))
    _logger.LogDebug("Route decision: {Reason}, candidates=[{Names}]", ...);
```

### Bad: String interpolation

```csharp
_logger.LogWarning($"Model {name} failed (status {status})");   // NOT structured
// Result: no named fields, no queryable log — breaks log aggregation (Splunk/Loki/ELK).
```

---

## 6. Tests Required

### Assertion Points

| Test | Asserts |
|------|---------|
| `RouterOptionsValidator` unknown Tag | `LogWarning` emitted listing unknown + known tags |
| `RouterOptionsValidator` default ctor | Uses `NullLogger` — no NRE, no logging side effect |
| Config validation | No log spam on every `Validate` call; warning only on the unknown-tag path |
| Component test via `ILoggerFactory` | (Optional) capture `ILoggerProvider` to assert a specific message was emitted |

### Logging-Specific Test Note

- No logging assertions in the existing suite (logging is observational). Prefer asserting **behavior**, not log text.
- If a regression needs a log assertion, inject a fake `ILogger<T>` and capture via `ILoggerProvider`, or assert on the `LogLevel` gate.

---

## 7. Wrong vs Correct

### Wrong: Logging API keys or bearer tokens

```csharp
_logger.LogInformation("Auth attempt: token={Token}", providedKey);  // NEVER
// Authorization headers, ApiKeys, and ?key= values must never reach logs.
```

### Correct: Log only existence, never the secret

```csharp
// API endpoints return HasApiKey bool, never the key itself.
_logger.LogInformation("Model {Name} configured", model.Name); // key value never logged
```

### Wrong: Unstructured interpolation in hot path

```csharp
_logger.LogDebug($"Decision: {decision.Reason}");  // NOT structured, always allocates
```

### Correct: Guarded structured debug

```csharp
if (_logger.IsEnabled(LogLevel.Debug))
    _logger.LogDebug("Decision: {Reason}", decision.Reason);
```

---

## Design Decisions

### Decision: Chinese log messages, English docs

**Context**: README states docs in English, but log strings are Chinese (e.g. `模型 {Name} 含未识别的 Tags`).

**Decision**: Keep log message text in Chinese (team convention), keep structured placeholders in English. The named-placeholder structure is what matters for log aggregation, not the message language.

### Decision: `IsEnabled(LogLevel.Debug)` guard

**Context**: Route-decision logging builds a `string.Join` over the candidate list — cheap at Information level but wasteful per-request at Debug if debug is off.

**Decision**: Guard expensive Debug messages with `_logger.IsEnabled(LogLevel.Debug)`. Cheap Debug messages (single scalar) skip the guard.

### Decision: Warning (not Error) for per-candidate failures

**Context**: A single model failing is normal during failover — the request still succeeds via the next candidate.

**Decision**: Per-candidate failures are `LogWarning` (recoverable, expected). Only `AllCandidatesFailedException` reaching the boundary is an error. `LogError` reserved for infra failures (config load/reload).

### Decision: Never log secrets

**Context**: Authorization headers, model ApiKeys, and `?key=` query params are authentication material.

**Decision**: Never log key values. APIs return `HasApiKey: bool` only. `?key=` URL usage is documented as "log risk by caller/reverse-proxy responsibility" — the app itself never echoes it.