# Implementation Plan

## Wave 0 — baseline

- [x] Snapshot tracked/untracked state and run the focused baseline tests.
- [x] Record exact affected files before each delegated patch.

## Wave 1 — P0 routing safety

- [x] Delegate monotonic eligibility enforcement to `luna_worker` with ownership
      of `RouterEngine.cs`, `RouterContext.cs`, and focused routing tests.
- [x] Verify data-sovereignty + failover and data-sovereignty + budget-degrade
      regressions, then run all routing tests.
- [x] Main agent reviews candidate-set semantics and public-contract impact.

## Wave 2 — P0 model configuration

- [x] Delegate authoritative options replacement and complete field mapping to
      `luna_worker` with ownership of the provider/composition-root patch and
      focused configuration tests.
- [x] Verify empty list, shrink, delete, locality change, and hot reload.
- [x] Main agent checks DI lifetime/reload behavior and startup validation.

## Wave 3 — P1 durability and accounting

- [x] Delegate audit batch retry to one `luna_worker` assignment owning only
      `SqliteRequestAuditStore.cs` and its tests.
- [x] Delegate missing-stream-usage cost fallback to a separate assignment
      owning `ProxyOrchestrator.cs` and endpoint tests.
- [x] Delegate bounded non-stream reads to a separate assignment owning
      `OpenAICompatibleModelClient.cs` and client tests.
- [x] Run endpoint, client, audit, and smoke test groups together.

## Wave 4 — P2 tenant enforcement

- [x] Main agent fixes the internal contract: tenant identity location,
      constant-time authentication result, QPS/budget response behavior, and
      cost-attribution boundary.
- [x] Delegate `ClientKeyService` persistence/auth/quota implementation and unit
      tests to `luna_worker`.
- [x] Delegate middleware wiring and integration tests to `luna_worker` after
      the service contract is accepted.
- [x] Delegate `OutcomeRecorder` tenant spend attribution and focused tests to
      `luna_worker` after middleware identity is stable.
- [x] Verify admin-route isolation, global-key compatibility, parallel strategy
      cost attribution, and absence of plaintext-key logs.

## Wave 5 — P2 configuration hardening

- [x] Delegate serialized atomic model updates to `luna_worker` with ownership
      of `ModelsConfigService.cs` and configuration tests.
- [x] Delegate model endpoint validation to `luna_worker` with ownership of
      `RouterOptionsValidator.cs` and validator tests.
- [x] Run configuration, dashboard endpoint, and hot-reload tests.

## Wave 6 — P3 dependency hygiene

- [x] Delegate the test-project dependency upgrade to `luna_worker`, limited to
      the test project file and compilation adaptations required by WireMock.
- [x] Run restore, build, full tests, and transitive vulnerability scan.

## Final integration gate

- [x] Inspect the complete diff; reject unrelated formatting/refactoring.
- [x] Run `dotnet test OptiRouter.sln -c Release --no-restore`.
- [x] Run `dotnet build OptiRouter.sln -c Release --no-restore -warnaserror`.
- [x] Run `dotnet list package --vulnerable --include-transitive`.
- [x] Check that only task-owned files plus Trellis artifacts changed.
- [x] Review whether the routing/config/security contracts should be added to
      `.trellis/spec/` before proposing commits.
