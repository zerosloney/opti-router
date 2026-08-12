# 修复深度审查发现

## Goal

Close the confirmed review findings in priority order without changing the
OpenAI-compatible HTTP response contract or introducing speculative features.

## Requirements

### P0 — security invariants

- Routing eligibility is monotonic: a model removed by data-sovereignty,
  capability, long-input, quota, health, or failed-model filtering cannot be
  reintroduced by budget degradation, failover, ordering, or affinity.
- `models-config.json` is authoritative after first-run seeding. Empty and
  shortened arrays must not resurrect lower-priority `appsettings.json` models.
- Every model field, including `IsLocalOrPrivate`, survives runtime binding and
  hot reload.

### P1 — accounting and I/O reliability

- A transient SQLite audit flush failure must not silently discard dequeued
  audit records. At-least-once retry is acceptable; silent loss is not.
- Successful streams without an upstream usage block must record estimated
  input cost and mark the audit record as estimated.
- Non-streaming upstream response bodies must have an enforced byte ceiling
  before full materialization.

### P2 — configuration and tenant enforcement

- Tenant client keys authenticate `/v1` requests while admin routes continue to
  require the configured admin/proxy key.
- Enabled tenant keys enforce their own QPS and UTC-daily budget; actual costs
  recorded for that request update tenant spend.
- Plaintext client keys are returned only once by the creation API and are never
  written to logs or persisted. New installations start with no undisclosed
  seeded keys.
- Model upsert/delete operations are serialized as one read-modify-write and
  file replacement is atomic within the target directory.
- Startup and dashboard writes reject invalid model BaseUrl, timeout, and retry
  values at the existing validation boundary.

### P3 — dependency hygiene

- Upgrade the existing test dependency chain so the confirmed Scriban and
  System.Linq.Dynamic.Core advisories are absent from the effective test graph.
- Remove blanket NU190x suppression when the resulting graph is clean.

## Constraints

- Preserve the existing `/v1/chat/completions`, dashboard DTO, and SSE payload
  shapes.
- Preserve global `ProxyApiKey` behavior for backward compatibility.
- No database schema migration or net-new library capability. A direct safe
  version pin of a package already present in the effective graph is allowed
  when required to remove a confirmed advisory.
- Reuse existing policy, cost, audit, options, and rate-limiting seams.
- Keep changes limited to the responsible modules and regression tests.
- Existing unrelated untracked `.trellis/workspace` files are out of scope.

## Acceptance Criteria

- [x] Combined routing tests prove data-sovereignty and other hard filters hold
      under failover and budget-degrade paths.
- [x] Configuration tests prove empty, shortened, and locality-changing model
      files are reflected identically by management reads and `IOptionsMonitor`.
- [x] Audit fault-injection test proves a failed batch is retried rather than
      lost.
- [x] Streaming test without usage records non-zero estimated input cost with
      `IsEstimated=true`.
- [x] Oversized non-streaming success and error bodies fail with the dedicated
      size-limit category without unbounded buffering.
- [x] Tenant-key integration tests cover authentication, disabled keys, QPS,
      daily budget, spend recording, UTC rollover, and global-key compatibility.
- [x] Concurrent model updates do not lose either write; failed/interrupted
      writes do not replace the last valid file.
- [x] Validator tests cover malformed/non-HTTP BaseUrl, non-positive timeout,
      and negative retries.
- [x] `dotnet list package --vulnerable --include-transitive` no longer reports
      the two confirmed test-only advisory chains.
- [x] `dotnet test OptiRouter.sln -c Release --no-restore` passes.
- [x] `dotnet build OptiRouter.sln -c Release --no-restore -warnaserror` passes
      with zero warnings and errors.

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
