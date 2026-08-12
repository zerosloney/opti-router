# Confirmed Review Findings

Baseline on 2026-08-12:

- Release build: zero warnings and errors.
- Tests: 503 passed, none skipped.
- Coverage: 72.01% line, 61.63% branch.
- Production dependency scan: no reported known vulnerabilities.
- Test dependency scan: WireMock.Net 1.6.7 pulls vulnerable
  Scriban.Signed 5.5.0 and System.Linq.Dynamic.Core 1.3.12.

Confirmed code paths:

1. `FailoverPolicy` and `BudgetGuardPolicy` rebuild from
   `RouterContext.AllModels`, allowing earlier hard filters to be undone.
2. `ModelsJsonConfigurationProvider` cannot tombstone lower-provider array
   entries and omits `IsLocalOrPrivate`.
3. `SqliteRequestAuditStore.FlushQueue` dequeues before transaction commit and
   does not restore records on failure.
4. `ProxyOrchestrator.StreamAsync` records cost only when final usage is present.
5. `OpenAICompatibleModelClient` materializes non-stream success/error content
   through unbounded `ReadAsStringAsync`.
6. `ClientKeyService` is management-only: hashes and quotas are not consumed by
   request authentication/accounting; default plaintext keys are logged.
7. `ModelsConfigService` does not hold one lock across read-modify-write and uses
   direct non-atomic target writes.
8. `RouterOptionsValidator.ValidateModel` omits URI, timeout, and retry checks.
9. The test project suppresses NU1902/NU1903/NU1904 while vulnerable transitive
   packages are present.

Primary specifications consulted:

- `.trellis/spec/backend/routing.md`
- `.trellis/spec/backend/database-guidelines.md`
- `.trellis/spec/backend/logging-guidelines.md`
- `.trellis/spec/backend/error-handling.md`
- `.trellis/spec/backend/quality-guidelines.md`
- `.trellis/spec/guides/cross-layer-thinking-guide.md`
