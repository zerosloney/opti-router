# Routing Foundation MVP — Implementation Plan

## Ordered Checklist

1. **Baseline and overlap audit**
   - Capture `git status --short` and `git diff --stat`.
   - Run the existing test suite before edits; record any pre-existing failures.
   - Inspect all current uncommitted Fusion/Cascade/Race changes and preserve them.

2. **Add configuration contracts**
   - Add provider/family and cache pricing fields.
   - Add routing switches, TTL/minimum bounds, validation, binding tests, example config, and README documentation.
   - Keep candidate-order-changing features disabled by default.

3. **Normalize upstream metadata**
   - Extend usage DTOs and raw response wrappers compatibly.
   - Parse cache-token variants and known rate-limit/reset/retry headers in one client-owned normalization path.
   - Add client unit tests for supported, missing, and malformed values.

4. **Extend cost and audit contracts**
   - Implement cache-aware cost calculation with legacy fallbacks.
   - Add TTFT/cache fields to audit records, in-memory/SQLite storage, additive migration, dashboard/API DTOs, and metrics where bounded.
   - Add cost, migration, and round-trip tests.

5. **Implement quota capacity state and policy**
   - Add immutable snapshots and thread-safe process-local store.
   - Add zero-I/O quota-aware policy and register it in the intended chain position.
   - Split 429 handling from network/timeout/5xx health failures across serial, Race, Fusion, Cascade, and streaming paths.
   - Add deterministic policy and orchestrator regression tests.

6. **Implement stable-prefix cache affinity**
   - Add canonical stable-material SHA-256 fingerprint helper.
   - Add bounded-memory affinity recording and policy reordering.
   - Wire success recording through shared outcome/orchestration paths.
   - Test privacy, TTL, candidate filtering, and downstream override behavior.

7. **Implement Fusion panel selector**
   - Add typed request complexity to routing contracts.
   - Add fixed compatibility, dynamic size, and soft provider/family diversity selection.
   - Integrate with half-open probe admission without regressing attempted-model accounting.
   - Add focused selector and Fusion integration tests.

8. **Cross-flow verification**
   - Run targeted client/config/routing/endpoint/store tests after each section.
   - Run formatting/analyzers if configured.
   - Run full `dotnet test`.
   - Inspect the final diff for unrelated changes, secret leakage, reason-string parsing, blocking I/O, and unbounded labels.

## Validation Commands

```powershell
dotnet test tests/OptiRouter.Tests/OptiRouter.Tests.csproj --no-restore
dotnet test
git diff --check
git status --short
```

If `--no-restore` cannot run because dependencies are absent, run the full `dotnet test` with restore and report the reason.

## Review Gates

- Gate A: client metadata and cost tests pass before routing behavior changes.
- Gate B: 429-vs-health regressions pass before enabling quota policy tests.
- Gate C: fixed Fusion selection is byte-for-behavior compatible when new switches are off.
- Gate D: full suite and diff inspection pass before implementation is reported complete.

## Rollback Points

- After steps 2–4: additive metadata/accounting only; routing order remains unchanged.
- After steps 5–6: each policy can be disabled independently.
- After step 7: dynamic/diverse selection switches off to restore fixed first-N behavior.

## Ownership

`luna-worker` owns implementation in `src/OptiRouter/**`, `tests/OptiRouter.Tests/**`, README, and example configuration for this task. It is not alone in the worktree and must not revert other agents' or the user's edits. It must not commit or push.
