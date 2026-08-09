Active task: .trellis/tasks/08-09-routing-foundation

Implement the Routing Foundation MVP exactly as specified in `prd.md`, `design.md`, and `implement.md`.

Execution contract:

1. Read `implement.jsonl` and every referenced spec before editing.
2. Read the task PRD/design/implementation plan completely.
3. Treat the current dirty working tree as user-owned. You are not alone in the codebase: preserve all existing uncommitted Fusion/Cascade/Race edits, do not revert or overwrite changes made by others, and adapt your implementation to them.
4. Follow the ordered gates in `implement.md`. Capture baseline test failures before edits.
5. Candidate-order changes must default off. Legacy config and fixed Fusion behavior must remain compatible.
6. Do not parse `RouterDecision.Reason` as a behavioral contract. Routing policies perform memory-only reads.
7. A 429 updates quota state and triggers request failover, but must not increment the health circuit or model-health Thompson failure state. Network/timeouts/5xx retain current behavior.
8. Do not log raw prompts, raw rate-limit headers, API keys, or authorization data.
9. Do not add dependencies, commit, push, reset, checkout, delete files, or perform destructive cleanup.
10. If an implementation choice is not resolved by the artifacts/specs, report it instead of inventing architecture.

Authorized implementation scope:

- `src/OptiRouter/**`
- `tests/OptiRouter.Tests/**`
- `README.md`
- `src/OptiRouter/appsettings.example.json`
- `src/OptiRouter/appsettings.json` only if required to keep shipped defaults coherent

Required completion report:

- Files modified and purpose
- Baseline and final commands with exact pass/fail counts
- Acceptance criteria mapping
- Known limitations or unresolved issues
- Explicit confirmation that no commit/push/revert/destructive operation was performed

Do not stop at a partial phase if later failures are ordinary compilation/test regressions caused by your edits; fix them and run the full suite. Stop and report only when blocked by an unresolved product/architecture decision, two repeated tool failures, or scope expansion outside the authorized paths.
