# Error Handling

> How errors are handled in this project.

---

## Error Types

| Type | When | HTTP Status | Notes |
|------|------|-------------|-------|
| `AllCandidatesFailedException` | All candidate models in the chain failed (timeout, error, or empty response) | 502 | Includes `Attempts` list with per-model error details. Never thrown when at least one candidate succeeds. |
| `BudgetExhaustedException` | Daily/session budget exhausted and `EnforceOnExhausted == Reject` | 429 | Contains `Message` with budget state. Never thrown when `EnforceOnExhausted == Degrade` (policy downgrades to Cheap tier instead). |
| `ModelClientException` | Upstream model API returned an error (non-200, auth failure, parse failure) | — | Internal. Includes `ModelName`, `StatusCode`, `ResponseBody`. Caught by `ProxyOrchestrator` during failover. |
| `RouterOptionsValidator` validation failures | Config validation at startup | — | Returns `ValidateOptionsResult.Fail(reason)`. All validation errors are blocking (startup fails) except unknown Tags (warning only). |

---

## Error Handling Patterns

### Failover Chain

```csharp
// ProxyOrchestrator iterates candidate chain:
// 1. Try candidate[0]
// 2. On ModelClientException/HttpRequestException → try candidate[1]
// 3. Continue until one succeeds or all fail
// 4. All fail → throw AllCandidatesFailedException

// Circuit breaker: ModelHealthTracker tracks failures per model
//   FailoverPolicy excludes Open models from candidate chain
//   HealthTracker.RecordFailure → increments failure count
//   HealthTracker.RecordSuccess → resets failure count (Closed) or half-open probe
```

### Budget Exhaustion

```csharp
// BudgetGuardPolicy checks budget before failover
// Degrade mode: candidates filtered to Cheap tier only
// Reject mode: RouterDecision.BudgetExhausted = true
// ProxyOrchestrator checks BudgetExhausted → throws BudgetExhaustedException
// Exception filter → 429 response with retry-after header
```

### Thompson Sampling Outcome Recording

```csharp
// Recorded on every attempt (success or failure) in ProxyOrchestrator:
// isGood = success AND (elapsedMs < ThompsonLatencyTargetMs)
//   → Alpha += 1 (fast success)
// isGood = false on:
//   - success but slow (elapsedMs >= target)
//   - ModelClientException (non-200, auth failure, parse failure)
//   - HttpRequestException (network failure)
//   - OperationCanceledException (timeout)
//   - Fusion candidate cancelled (not adopted)
//   → Beta += 1 (slow, failed, or cancelled)

// Circuit breaker and Thompson state are complementary:
//   ModelHealthTracker: short-term exclusion (seconds/minutes cooldown)
//   Thompson Beta: long-term signal (hours, discount-weighted)
// Both record failures — this is intentional, not double-counting.
```

---

## API Error Responses

| Scenario | Status | Body |
|----------|--------|------|
| Missing/invalid `ProxyApiKey` | 401 | `{ "error": "Unauthorized" }` |
| Budget exhausted (Reject mode) | 429 | `{ "error": "Budget exhausted. Please try again later." }` |
| All models failed | 502 | `{ "error": "All candidate models failed.", "details": { "attempts": [...] } }` |
| Rate limit exceeded | 429 | `{ "error": "Rate limit exceeded. Retry after X seconds." }` |
| Request too large (streaming) | 413 | `{ "error": "Response stream exceeded maximum allowed bytes." }` |

---

## Common Mistakes

### Mistake: Checking `RouterDecision.Reason` string instead of `BudgetExhausted` flag

**Symptom**: Budget exhaustion detection breaks when `Reason` text is refactored.

**Fix**: Use `decision.BudgetExhausted` boolean property — the dedicated contract.

### Mistake: Swallowing `ModelClientException` without logging model name

**Symptom**: Cannot identify which upstream model is failing in production.

**Fix**: Always log model name + status code + truncated response body.

### Mistake: Not catching `HttpRequestException` for network-level failures

**Symptom**: DNS failure / connection reset crashes the request instead of triggering failover.

**Fix**: `ProxyOrchestrator` catches both `ModelClientException` and `HttpRequestException` in the failover loop.