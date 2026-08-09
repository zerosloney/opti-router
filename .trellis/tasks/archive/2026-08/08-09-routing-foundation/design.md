# Routing Foundation MVP — Technical Design

## 1. Architecture

The change introduces four bounded mechanisms while retaining the existing policy-chain and OpenAI-compatible transport:

```text
Upstream response
  -> Clients: normalize usage + response headers
  -> Endpoints: record TTFT/cost/outcome and update quota/cache state
  -> Routing memory snapshots
  -> Policies: cache-affinity + quota-aware candidate reordering
  -> FusionPanelSelector: dynamic size + soft diversity
  -> Audit/metrics/dashboard projections
```

Dependency direction remains `Clients/Configuration -> Routing -> Endpoints composition`, with `Program.cs` as the only composition root. The normalized client metadata types must not depend on endpoint types.

## 2. Contracts

### 2.1 Model capabilities

Add optional configuration fields with backward-compatible defaults:

- `Provider`: free-form normalized string; empty means unknown.
- `Family`: free-form normalized string; empty means unknown.
- `CachedInputPricePerMillion`: nullable decimal; null falls back to normal input price.
- `CacheWriteInputPricePerMillion`: nullable decimal; null falls back to normal input price.

Routing switches should include explicit enables and bounded values for prefix affinity TTL and dynamic Fusion minimum size. Reuse `FusionRouterPanelSize` as the maximum to avoid two competing maximum settings.

### 2.2 Usage and response metadata

Extend `ChatUsage` additively with cache-hit, cache-write, and cache-miss/uncached counts. Normalize provider shapes once in the client parser. Clamp inconsistent optional breakdowns so calculated uncached tokens never become negative.

Introduce an immutable `UpstreamResponseMetadata` containing normalized request/token remaining values, reset instants/durations, retry-after, and response-header elapsed milliseconds when known. Attach it to `RawChatResponse` through an optional positional/default member or compatible property. Attach it to only the first `RawStreamLine` so callers can update capacity once without changing forwarded SSE content.

### 2.3 Audit

Add nullable/defaulted fields to `RequestAuditRecord` for TTFT and cache token breakdown. Use additive SQLite columns via the existing `ALTER TABLE` migration helpers. Old rows project zeros/nulls. Dashboard/API projections consume typed fields rather than parsing routing reasons.

## 3. Quota State

Create a thread-safe per-model `UpstreamQuotaStateStore` in `Routing/` with immutable snapshots and memory-only reads. It accepts normalized response metadata and explicit 429 retry/reset data from the endpoint layer.

`QuotaAwarePolicy` is inserted after latency/cache preference and before failover/load balancing. It:

1. Keeps unknown quota candidates in existing order.
2. Softly moves candidates with clearly insufficient known headroom behind viable peers.
3. Temporarily excludes only candidates with positively known exhausted state and an unexpired reset/retry time.
4. Restores candidates automatically when the snapshot expires.

HTTP 429 remains retryable for candidate failover but calls quota-state recording plus probe release, not `ModelHealthTracker.RecordFailure`. It does not update the latency/success Thompson posterior as a model-health failure. All non-429 retryable failures keep current behavior.

## 4. Prefix Affinity

Create one canonical fingerprint helper. It serializes only stable prompt material in deterministic order, hashes with SHA-256, and returns no raw material. Stable material includes ordered system messages and stable tool/schema extension fields where present. Unknown volatile extension fields are excluded rather than guessed.

`PromptCacheAffinityPolicy` uses an injected memory cache/state abstraction and reorders only the candidates supplied by previous policies. Success recording happens centrally through `OutcomeRecorder`/orchestrators. The policy is placed so Session affinity remains the explicit-session signal and quota/health/budget policies can override cache preference.

## 5. Fusion Panel Selection

Introduce `FusionPanelSelector` as a pure, deterministic component used by `FusionRouter` before health-probe admission.

- Compatibility mode: return the first fixed N candidates exactly as today.
- Dynamic mode: derive requested size from a structured `RequestComplexity` value carried by `RouterDecision`/`RouterContext`, not from the diagnostic reason string.
- Diversity mode: always keep the primary candidate, then greedily prefer a candidate introducing a new provider and/or family; ties preserve original candidate rank. Empty metadata contributes no diversity bonus.
- Health half-open admission remains in `FusionRouter`; if selected candidates cannot obtain admission, continue scanning ranked selector output until the requested size is filled or candidates are exhausted.

The first implementation may use deterministic complexity buckets already derivable in `RuleClassifierPolicy`; it must expose that result as a typed field rather than duplicate regexes or parse text.

## 6. Cost Calculation

For prompt tokens:

1. Charge explicit cache-write tokens at cache-write price/fallback.
2. Charge cache-hit tokens at cached-input price/fallback.
3. Charge the remaining non-negative prompt tokens at normal input price.
4. Charge output tokens unchanged.

If no breakdown exists, all prompt tokens use the normal input price exactly as before. DeepSeek cache-miss counts may be used as an explicit uncached count after consistency checks.

## 7. Rollout and Rollback

- Candidate-order-changing features default off.
- Accounting fields and audit migrations are additive and safe to deploy before enabling policies.
- Operators can disable cache affinity, quota-aware routing, dynamic Panel sizing, and diversity independently without removing schema fields.
- Process-local quota snapshots are lost on restart by design; routing falls back to unknown-headroom behavior.
- Rollback consists of disabling switches or reverting additive code; old binaries must tolerate extra SQLite columns.

## 8. Risks

- Header semantics vary across providers: normalize only documented names and treat unknown values as absent.
- Response-header latency is not literal first-token latency for non-streaming calls; expose it with unambiguous naming/documentation.
- Current uncommitted Fusion changes overlap this task. The implementer must edit in place and preserve their behavior/tests.
- Adding record parameters can cause widespread compile failures; prefer defaulted additive properties or update every constructor/test atomically.
