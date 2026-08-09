# Routing Foundation MVP

## Goal

Build the provider-aware routing foundation required to improve latency, cost accuracy, and Fusion quality without adding prompt rewriting or protocol-dependent speculative MoA. The router must observe cache/quota signals, keep quota exhaustion separate from model health, and select Fusion panels dynamically with soft provider/family diversity.

## Background

- The current OpenAI-compatible client returns response body and basic prompt/completion usage but discards response headers and cache-token breakdowns.
- Cost calculation charges all prompt tokens at one input price, so cache hits/writes cannot be accounted accurately.
- HTTP 429 is currently handled through the same failure path as network/5xx failures and can trip the model circuit breaker.
- Session affinity exists, but no stable-prefix cache affinity exists across sessions.
- Fusion Router waits for every selected panel and currently selects the first fixed N candidates.
- The working tree already contains uncommitted Fusion/Cascade/Race work. These edits belong to the user and must be preserved and integrated with, never reverted.

## Requirements

### R1 — Provider and pricing capabilities

- Extend model configuration with optional provider and family metadata used for routing diversity; custom/unknown values must remain supported.
- Add optional cached-input and cache-write input prices. Existing configurations without these fields must preserve current full-input pricing behavior.
- Add validated routing switches for cache affinity, quota-aware routing, dynamic Fusion panel sizing, and Fusion diversity. New behavior must default off unless it only improves accounting without changing candidate order.
- Document every new setting in the example configuration and README.

### R2 — Normalized upstream response metadata

- Parse supported cache-token usage shapes from OpenAI-compatible JSON, including nested cached-token details and DeepSeek-style hit/miss fields, without failing on missing or malformed optional fields.
- Parse known rate-limit response headers into one normalized, immutable metadata contract. Unknown headers must be ignored.
- Preserve upstream response transparency: client-visible response bodies and SSE data remain unchanged.
- Capture TTFT separately from total elapsed time for streaming requests at the first upstream SSE data item. For non-streaming requests, record response-header latency as the available TTFT proxy and name/document it accordingly.

### R3 — Accurate cost and audit data

- Cost calculation must charge cache-hit, cache-write, and uncached input tokens using configured prices with safe fallbacks.
- Extend audit records/stores/dashboard DTOs as needed so TTFT and cache-token counts survive in-memory and SQLite round trips.
- SQLite schema changes must be additive and backward compatible through the existing incremental migration pattern.
- Existing audit rows and configurations remain readable.

### R4 — Quota capacity separated from health

- Maintain per-model in-memory quota snapshots from normalized response metadata; routing policies perform no network or disk I/O.
- Add a quota-aware policy that softly deprioritizes candidates whose known request/token headroom cannot satisfy the request and excludes a candidate only while a positively known exhaustion/reset window is active.
- A 429 response updates quota/cooldown capacity state and remains eligible for request-level failover, but must not increment the model health circuit-breaker failure count.
- Network errors, timeouts, and 5xx responses retain existing health/Thompson behavior.
- Concurrent Fusion/Race calls must update the same thread-safe quota state.

### R5 — Stable-prefix cache affinity

- Compute a privacy-safe SHA-256 fingerprint from stable prompt material (system messages plus stable tool/schema request fields); never store raw prompt content in the affinity cache.
- Successful requests record the selected model for that fingerprint with a bounded TTL.
- When enabled, the policy softly promotes the remembered model only if it remains in the already-filtered candidate set; downstream budget, quota, long-context, and health constraints can override it.
- Existing Session-ID affinity behavior remains compatible and independently configurable.

### R6 — Dynamic and diverse Fusion panels

- Replace direct `Take(panelSize)` selection with a dedicated deterministic selector.
- When dynamic sizing is enabled, choose within configured minimum/maximum bounds using structured request complexity/uncertainty data, never by parsing `RouterDecision.Reason`.
- When diversity is enabled, prefer distinct provider and family values while preserving the primary candidate and never selecting a lower-ranked model solely for diversity when required metadata is absent.
- Diversity is a soft constraint: insufficient eligible diversity must fall back to the best remaining candidate order.
- Existing fixed-N behavior must remain unchanged when both new switches are disabled.

### R7 — Safety, compatibility, and observability

- No prompt content, API keys, authorization headers, or raw rate-limit headers may be logged.
- Routing policies read memory snapshots only.
- Metrics/logging distinguish health failures, quota exhaustion, cache hits, cache writes, TTFT, and total latency without unbounded metric labels.
- Existing OpenAI-compatible requests, streaming format, failover, budgets, Cascade, Fusion-lite, and Fusion Router remain compatible.

## Acceptance Criteria

- [ ] Existing configuration loads unchanged and all existing tests pass.
- [ ] Config binding and validation tests cover every new setting and invalid boundary.
- [ ] Client tests cover OpenAI-style cached tokens, DeepSeek-style hit/miss tokens, known rate-limit headers, absent headers, malformed optional usage, and streaming first-item TTFT behavior.
- [ ] Cost tests prove cached/write/uncached token pricing and legacy fallback behavior.
- [ ] In-memory and SQLite audit round-trip tests preserve TTFT and cache-token fields; migration works against a pre-change database.
- [ ] A 429 causes candidate failover and quota state update without opening/incrementing the health circuit; a 5xx still increments health failure state.
- [ ] Quota-aware routing is deterministic under fixed snapshots and never performs I/O in `Apply()`.
- [ ] Prefix affinity stores only hashes, respects TTL and candidate filters, and is overridden by quota/health/budget constraints.
- [ ] Fusion selector tests cover fixed compatibility, simple/complex dynamic sizes, missing metadata, provider diversity, family diversity, insufficient candidates, and half-open admission.
- [ ] Non-streaming, streaming, Race, Cascade, and Fusion smoke tests remain green.
- [ ] `dotnet test` succeeds for the complete repository.

## Out of Scope

- Rewriting, pruning, summarizing, or otherwise modifying user prompts.
- Online price scraping or synchronous routing to asynchronous Batch APIs.
- Implicit user-satisfaction detection or contextual quality-bandit updates.
- ONNX/SLM embedding inference.
- Speculative Panel-to-Analyst duplex streaming or revisable client-visible answers.
- Distributed quota coordination across multiple OptiRouter replicas; this MVP provides process-local snapshots and clearly documents that limit.

## Delivery Constraints

- The implementer must not revert or overwrite unrelated existing working-tree changes.
- No git commit, push, reset, checkout, or destructive cleanup is authorized.
- Prefer additive contracts and constructor-compatible defaults to minimize test and downstream breakage.
