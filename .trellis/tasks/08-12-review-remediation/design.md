# Review Remediation Design

## Scope and boundaries

This task fixes nine confirmed findings. It preserves external HTTP payloads,
uses the existing SQLite/configuration/routing abstractions, and introduces no
net-new library capability. A safe direct version pin is permitted for a
package already present in the test dependency graph when needed to clear a
confirmed advisory. Implementation is delivered in independently verifiable
waves so each `luna_worker` assignment has narrow file ownership.

## 1. Monotonic routing eligibility

`RouterEngine` owns the safety invariant. During the Filter group, the context's
available-model pool is narrowed after each policy. A filter result is
intersected with the preceding pool, so a filter cannot add a model removed by
an earlier filter. The final filtered pool becomes the only pool visible to
Classify, Order, and Constraint policies. Any candidates produced afterward are
intersected with that pool before the next policy.

This preserves budget degradation across eligible tiers while preventing
`FailoverPolicy` and `BudgetGuardPolicy` from escaping through the original
all-model list. Empty candidates continue to flow to the established
`AllCandidatesFailedException` boundary.

No policy interface signature changes. `RouterContext.AllModels` is clarified
as the currently eligible pool for the active policy phase, initially all
enabled models.

## 2. Authoritative model configuration

First-run seeding remains unchanged. `models-config.json` then becomes the
authoritative model list at the options boundary:

1. Normal `RouterOptions` binding supplies non-model settings.
2. A post-configure step replaces `RouterOptions.Models` with the list loaded by
   `ModelsConfigService`.
3. The existing configuration provider remains the reload trigger and maps all
   fields, including `IsLocalOrPrivate`, for direct configuration consumers.

Replacement, rather than layered array merging, makes empty and shortened lists
unambiguous. Invalid/corrupt authoritative files fail validation instead of
silently resurrecting appsettings models.

## 3. Audit durability

`SqliteRequestAuditStore` drains a bounded local batch. The batch is committed
in one transaction. If insert or commit fails, every drained record is put back
and the worker is signalled for retry. This gives at-least-once delivery; a rare
ambiguous commit may duplicate audit rows, which is safer than silent loss and
does not affect cost accounting.

## 4. Usage fallback and response limits

Streaming uses the same input-token estimate already carried by
`RouterDecision` when no final usage chunk is received. It records estimated
input cost and sets `IsEstimated=true`; actual usage remains authoritative.

Non-streaming content is read through a bounded stream helper before JSON/error
materialization. Both success and error bodies share the limit and throw
`ResponseSizeLimitExceededException`. The first patch reuses the established
response-size configuration where available; if the client boundary cannot
receive it without expanding public contracts, use one documented fixed ceiling
and leave configurability out of scope.

## 5. Tenant authentication and quota path

`ClientKeyService` owns persisted key state and runtime quota state:

- Constant-time SHA-256 authentication returns an immutable key identity.
- New files contain an empty list; no plaintext seed is logged.
- Existing hashed keys remain valid.
- A per-key UTC fixed window enforces `MaxQps`.
- Daily spend has an associated UTC date and resets on first access after
  rollover. Mutations are lock-protected and atomically persisted.

The authentication middleware accepts the global proxy key first, then an
enabled tenant key for `/v1` only. It stores the immutable identity in
`HttpContext.Items`. Admin routes never accept tenant keys.

`OutcomeRecorder` uses `IHttpContextAccessor` to attribute every cost recorded
inside the current request—including Race/Fusion/Cascade calls—to that tenant.
Requests without a tenant identity retain current behavior. This avoids changing
the orchestrator and public endpoint signatures.

## 6. Atomic model writes and validation

`ModelsConfigService` holds one lock across each upsert/delete read-modify-write.
Serialization goes to a same-directory temporary file, is flushed, then replaces
the target atomically. Temporary files are cleaned on failure.

`RouterOptionsValidator.ValidateModel` additionally requires an absolute HTTP or
HTTPS BaseUrl, `TimeoutSeconds > 0`, and `MaxRetries >= 0`. Dashboard writes reuse
this method, keeping one validation owner.

## 7. Test dependency remediation

Upgrade the existing `WireMock.Net` dependency to a compatible current version,
restore/build the test project, and adapt only API usages that no longer compile.
Run the vulnerability scan after restore. NU190x suppression is removed only
when the effective graph is clean; no replacement mocking dependency is added.

## Compatibility and rollback

- Each wave is independently testable and revertible.
- Global API keys, endpoint routes, response DTOs, and SQLite schema are kept.
- Existing client-key JSON gains only backward-compatible optional metadata.
- If a wave breaks the full suite, revert that wave rather than compensating in
  unrelated modules.
