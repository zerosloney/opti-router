# Error Handling

> How errors are handled in this project.

---

## Error Types

| Type | When | HTTP Status | Notes |
|------|------|-------------|-------|
| `AllCandidatesFailedException` | All candidate models in the chain failed (timeout, error, or empty response) | 502 | Includes `Attempts` list with per-model error details. Never thrown when at least one candidate succeeds. |
| `BudgetExhaustedException` | Daily/session budget exhausted and `EnforceOnExhausted == Reject` | 429 | Contains `Message` with budget state. Never thrown when `EnforceOnExhausted == Degrade` (policy downgrades to Cheap tier instead). |
| `ModelClientException` | Upstream model API returned an error (non-200, auth failure, parse failure) | — | Internal. Includes `ModelName`, `StatusCode`, `ResponseBody`. Caught by `ProxyOrchestrator` during failover. |
| `ResponseSizeLimitExceededException` | Streaming response exceeded `MaxResponseStreamBytes` (cumulative) or `MaxStreamLineBytes` (single line) | 200 (in-band SSE error event) | `Clients/` ns. Carries `LimitBytes`. Thrown mid-stream by `ProxyOrchestrator` + `OpenAICompatibleModelClient`. Mapped to `RESPONSE_TOO_LARGE` code — NOT retryable. Use this dedicated type, not `InvalidOperationException`. |
| `RouterOptionsValidator` validation failures | Config validation at startup | — | Returns `ValidateOptionsResult.Fail(reason)`. All validation errors are blocking (startup fails) except unknown Tags (warning only). |

---

## Upstream quota versus availability contract

### 1. Scope / Trigger

- Applies to every upstream call site: serial and streaming proxy attempts, Race, Fusion panel/analyst/outer, Cascade verification/upgrade, and background health probes.
- The purpose is to prevent an HTTP 429 capacity signal from being treated as endpoint unavailability while retaining ordinary 5xx/network/timeout failover behavior.

### 2. Signatures

```csharp
internal static bool UpstreamFailureClassifier.IsQuotaLimited(Exception? error);
internal static string UpstreamFailureClassifier.SafeMessage(Exception? error, bool quotaLimited);
internal static int UpstreamFailureClassifier.GetStatus(Exception error);

public void OutcomeRecorder.RecordQuota(
    string modelName, UpstreamResponseMetadata? metadata, bool rateLimited = false);
```

### 3. Contracts

- `ModelClientException` with status 429 is quota-limited: update `UpstreamQuotaStateStore`, write a safe audit failure with `QuotaLimited=true`, then continue the applicable fallback path.
- A 429 must not call model-health failure or Thompson failure recording. Quota exhaustion and endpoint availability are separate state machines.
- Non-429 `ModelClientException`, `HttpRequestException`, timeout, and other upstream failures retain health/Thompson feedback and failure audit metrics.
- `SafeMessage` exposes only normalized categories/status codes. Never propagate or log raw response bodies, headers, keys, or prompt content from this classifier.

### 4. Validation & Error Matrix

| Failure | Quota state | Health/Thompson | Safe category |
|---------|-------------|-----------------|---------------|
| HTTP 429 with reset metadata | exhausted until normalized reset | unchanged | `quota-exhausted` |
| HTTP 429 without reset metadata | quota event recorded, no invented reset | unchanged | `quota-exhausted` |
| HTTP 500/502/503 | metadata may still be observed | failure recorded | `upstream-status-NNN` |
| Network exception | unchanged | failure recorded | `network-error` |
| Timeout/cancellation owned by upstream attempt | unchanged | failure recorded | `timeout` |

### 5. Good/Base/Bad Cases

- **Good**: Race receives 429 from A and 200 from B; A is marked quota-limited only, B is adopted, and A's circuit remains closed.
- **Base**: A returns 503; the shared classifier records health/Thompson failure and normal failover selects B.
- **Bad**: Fusion analyst catches 429 in a generic catch that calls `RecordFailure`; this makes auxiliary calls behave differently from the main proxy and opens the circuit incorrectly.

### 6. Tests Required

- For streaming, Race, Fusion, Cascade, and probe paths, assert 429 updates quota/audit state without health/Thompson failure.
- For the same paths, assert 5xx still updates health/Thompson failure and returns/falls back according to the existing API contract.
- Assert safe messages never contain the upstream response body, raw header values, API keys, or prompt text.

### 7. Wrong vs Correct

```csharp
// Wrong: quota is treated as availability failure.
catch (Exception ex)
{
    health.RecordFailure(modelName);
    thompson.RecordOutcome(modelName, false, discount);
}

// Correct: classify once at every orchestration boundary.
catch (Exception ex)
{
    bool quotaLimited = UpstreamFailureClassifier.IsQuotaLimited(ex);
    recorder.RecordQuota(modelName, metadata, quotaLimited);
if (!quotaLimited) recorder.RecordFailure(modelName, ex);
}
```

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
// Recorded on every adopted success and every non-429 failure:
// isGood = success AND (elapsedMs < ThompsonLatencyTargetMs)
//   → Alpha += 1 (fast success)
// isGood = false on:
//   - success but slow (elapsedMs >= target)
//   - ModelClientException except HTTP 429 (auth failure, parse failure, other non-200)
//   - HttpRequestException (network failure)
//   - OperationCanceledException (timeout)
//   - Fusion candidate cancelled (not adopted)
//   → Beta += 1 (slow, failed, or cancelled)

// Circuit breaker and Thompson state are complementary:
//   ModelHealthTracker: short-term exclusion (seconds/minutes cooldown)
//   Thompson Beta: long-term signal (hours, discount-weighted)
// Both record non-429 availability failures — this is intentional, not double-counting.
// HTTP 429 updates quota state only.
```

---

## SSE Mid-Stream Error Contract

### 1. Scope / Trigger
- **Trigger**: Streaming responses (`stream: true`). Once the first chunk is yielded, HTTP status (200) and headers are flushed and cannot be rolled back — failover to another model is impossible.
- **Why code-spec depth**: This is a cross-layer contract (orchestrator exception → endpoint in-band error event → client retry decision). Any new exception type or error source must flow through `ClassifyMidStreamError` or clients silently get the wrong retry signal.

### 2. Signatures
```csharp
// Endpoints/ChatCompletionsEndpoint.cs — mid-stream exception → code classification
private static string ClassifyMidStreamError(Exception ex)

// Endpoints/ChatCompletionsEndpoint.cs — writes the in-band error event + [DONE]
private static async Task WriteErrorAsync(Stream stream, string message, string code, CancellationToken ct)
// Emits: data: {"error":{"message":...,"type":...,"code":...}}\n\n  then  data: [DONE]\n\n
```

### 3. Contracts
- **HTTP**: always 200 + `text/event-stream` once first chunk flushed. Error is in-band, not in HTTP status.
- **Error event shape**: `data: {"error":{"message":<string>,"type":<string>,"code":<string>}}` — OpenAI-compatible nested object. The legacy `{"error":"<string>"}` (bare string) is forbidden; OpenAI SDKs fail to parse it.
- **`code` ↔ `type` mapping** (must stay in sync):

| `code` | `type` | Exception source | Retryable |
|--------|--------|------------------|-----------|
| `UPSTREAM_ERROR` | `upstream_error` | `HttpRequestException` / `IOException` | yes |
| `TIMEOUT` | `timeout` | `OperationCanceledException` (HttpClient internal timeout, not external ct) | yes |
| `RESPONSE_TOO_LARGE` | `response_too_large` | `ResponseSizeLimitExceededException` | no |
| `INTERNAL_ERROR` | `server_error` | any other (incl. generic `InvalidOperationException`) | no |
| `BUDGET_EXHAUSTED` | `budget_exceeded` | `BudgetExhaustedException` | wait for reset |
| `ALL_CANDIDATES_FAILED` | `all_candidates_failed` | `AllCandidatesFailedException` (pre-first-chunk only) | check health |

- **Terminator**: every error event is followed by `data: [DONE]`. Clients must treat `[DONE]` as stream end; a connection drop before `[DONE]` without an error event is a transport-level failure (retry).

### 4. Validation & Error Matrix
| Condition | Classified as | Notes |
|-----------|---------------|-------|
| Upstream socket reset mid-stream | `UPSTREAM_ERROR` | `IOException` from PipeReader |
| HttpClient timeout mid-stream | `TIMEOUT` | `OperationCanceledException`, `!ct.IsCancellationRequested` |
| Cumulative bytes > `MaxResponseStreamBytes` | `RESPONSE_TOO_LARGE` | `ResponseSizeLimitExceededException` from orchestrator |
| Single SSE line > `MaxStreamLineBytes` (1MB) | `RESPONSE_TOO_LARGE` | `ResponseSizeLimitExceededException` from client |
| External ct cancelled (client disconnect) | n/a | Connection unwritable; catch block not reached |
| Generic `InvalidOperationException` (proxy bug) | `INTERNAL_ERROR` | NOT `RESPONSE_TOO_LARGE` — do not use IOE for size limits |

### 5. Good/Base/Bad Cases
- **Good**: `ResponseSizeLimitExceededException` thrown at size limit → client reads `RESPONSE_TOO_LARGE`, raises limit or inspects upstream output.
- **Base**: `IOException` mid-stream → `UPSTREAM_ERROR`, client retries same request.
- **Bad**: Throw `InvalidOperationException("size exceeded")` at a new size check → misclassified as `INTERNAL_ERROR` (client told not-retryable, but it actually is a size issue). Use the dedicated exception type.

### 6. Tests Required
- `tests/.../ChatCompletionsEndpointTests.cs::Post_Streaming_MidStreamFailure_InjectsErrorEventAndDone` — IOException → UPSTREAM_ERROR, asserts first chunk + error event + [DONE], nested error object shape.
- `...::Post_Streaming_MidStreamTimeout_InjectsTimeoutCode` — `OperationCanceledException` → TIMEOUT.
- `...::Post_Streaming_MidStreamSizeLimit_InjectsResponseTooLargeCode` — `ResponseSizeLimitExceededException` → RESPONSE_TOO_LARGE.
- `...::Post_Streaming_MidStreamGenericInvalidOperation_InjectsInternalErrorCode` — generic `InvalidOperationException` → INTERNAL_ERROR (regression lock: confirms IOE is NOT mapped to RESPONSE_TOO_LARGE).
- Assertion points: status 200, `text/event-stream`, `dataLines.Count >= 3` (first chunk + error + DONE), `error.code`/`error.type`/`error.message` exact match.

### 7. Wrong vs Correct
#### Wrong: bare-string error + dropped code
```csharp
var json = JsonSerializer.Serialize(new { error }); // code param ignored
// Emits: data: {"error":"<message>"}  — non-spec, OpenAI SDK parse failure, no retry signal
```
#### Correct: nested object with code/type
```csharp
string type = code switch { "TIMEOUT" => "timeout", "UPSTREAM_ERROR" => "upstream_error", ... };
var payload = new { error = new { message, type, code } };
// Emits: data: {"error":{"message":...,"type":...,"code":...}} — SDK-parseable, machine-readable retry signal
```

---

## Spec: Use ResponseSizeLimitExceededException for size limits (NOT InvalidOperationException)

**Problem**: `ProxyOrchestrator` (MaxResponseStreamBytes) and `OpenAICompatibleModelClient` (MaxStreamLineBytes) both enforce byte limits. Originally threw `InvalidOperationException`, which the endpoint approximated as `RESPONSE_TOO_LARGE` — but any *other* `InvalidOperationException` (proxy internal bug) would be mislabeled as a size limit.

**Convention**: All size-limit throw sites MUST use `ResponseSizeLimitExceededException` (carries `LimitBytes`). The endpoint's `ClassifyMidStreamError` matches this exact type → `RESPONSE_TOO_LARGE`. Generic `InvalidOperationException` falls through to `INTERNAL_ERROR`.

**Throw sites** (keep in sync if adding new limits):
- `ProxyOrchestrator.StreamAsync` — first-line check + per-line cumulative check (MaxResponseStreamBytes)
- `OpenAICompatibleModelClient.StreamAsync` — single-line check (MaxStreamLineBytes)
- `OpenAICompatibleModelClient.StreamRawAsync` — single-line check (MaxStreamLineBytes)

---

## API Error Responses

### Non-streaming (RFC 7807 ProblemDetails, `application/problem+json`)

| Scenario | Status | Body |
|----------|--------|------|
| Missing/invalid `ProxyApiKey` | 401 | auth middleware response |
| Budget exhausted (Reject mode) | 429 | `ProblemDetails` with `code=BUDGET_EXHAUSTED`, `Retry-After: 3600`, `retryAfterSeconds` extension |
| All models failed | 503 | `ProblemDetails` with attempted models + last failure detail |
| Upstream 4xx (pre-stream) | passthrough | `CreateUpstreamRejection` — returns upstream status + `application/problem+json` (no failover on 4xx) |

### Streaming (200 + `text/event-stream`, error in-band)

| Scenario | Status | SSE event |
|----------|--------|-----------|
| Mid-stream upstream failure | 200 | `data: {"error":{"message":...,"type":"upstream_error","code":"UPSTREAM_ERROR"}}` + `[DONE]` |
| Mid-stream timeout | 200 | `... "type":"timeout","code":"TIMEOUT"` ... |
| Size limit exceeded | 200 | `... "type":"response_too_large","code":"RESPONSE_TOO_LARGE"` ... |
| All candidates failed (pre-first-chunk) | 200 | `... "type":"all_candidates_failed","code":"ALL_CANDIDATES_FAILED"` ... |
| Budget exhausted (pre-first-chunk) | 200 | `... "type":"budget_exceeded","code":"BUDGET_EXHAUSTED"` ... |

> **Note**: Streaming errors are NEVER signaled via HTTP status — headers are flushed at first chunk. See "SSE Mid-Stream Error Contract" above for the full code/type matrix and client retry guidance.

---

## Common Mistakes

### Mistake: Checking `RouterDecision.Reason` string instead of `BudgetExhausted` flag

**Symptom**: Budget exhaustion detection breaks when `Reason` text is refactored.

**Fix**: Use `decision.BudgetExhausted` boolean property — the dedicated contract.

### Mistake: Logging raw `ModelClientException.ResponseBody`

**Symptom**: Provider payloads can contain prompt fragments, identifiers, or other sensitive data and leak through logs.

**Fix**: Log model name, normalized status/category, and request correlation only. Keep `ResponseBody` available for internal passthrough/error mapping where required, but never write it to logs, audit messages, or metrics.

### Mistake: Not catching `HttpRequestException` for network-level failures

**Symptom**: DNS failure / connection reset crashes the request instead of triggering failover.

**Fix**: `ProxyOrchestrator` catches both `ModelClientException` and `HttpRequestException` in the failover loop.

### Mistake: Throwing `InvalidOperationException` for size limits

**Symptom**: Size-limit errors get misclassified as `INTERNAL_ERROR` (not-retryable) by the streaming endpoint, because `ClassifyMidStreamError` only maps the dedicated `ResponseSizeLimitExceededException` to `RESPONSE_TOO_LARGE`. Generic `InvalidOperationException` falls through to `INTERNAL_ERROR`, hiding the real cause (upstream output too large) from the client.

**Fix**: All size-limit throw sites use `ResponseSizeLimitExceededException(maxBytes, message)`. See the spec section above. Never reuse `InvalidOperationException` for size limits — a future proxy bug throwing IOE would be mislabeled as a size issue (or vice versa).
