# Directory Structure

> Backend module layout, namespace conventions, and where new files go.

---

## 1. Scope / Trigger

- Single project `src/OptiRouter` (`.NET 8`, `Microsoft.NET.Sdk.Web`). No solution-level multi-project backend split.
- Folder-per-concern under `src/OptiRouter/`, each folder maps 1:1 to a `OptiRouter.<Folder>` namespace (file-scoped).
- Tests live in a separate project `tests/OptiRouter.Tests`, mirroring the `src` folder structure under `Routing/`, `Endpoints/`, `Clients/`, `Components/`, `Health/`, `Smoke/`, `Configuration/`.

---

## 2. Signatures / Layout

```
src/OptiRouter/
├── Program.cs                 # DI wiring, policy chain, middleware, rate limiting
├── Configuration/             # namespace OptiRouter.Configuration
│   ├── RouterOptions.cs       # root config (Models + Budget + Routing)
│   ├── RoutingOptions.cs      # routing toggles/params
│   ├── BudgetOptions.cs       # budget + store config
│   ├── ModelEndpointOptions.cs# single model endpoint
│   ├── Enums.cs               # ModelTier, BudgetExhaustionMode, etc.
│   ├── RouterOptionsValidator.cs  # startup config validation (IValidateOptions)
│   ├── ModelsConfigService.cs     # models-config.json read/write + hot reload
│   └── ModelsConfigurationProvider.cs  # maps models-config.json to IConfiguration
├── Routing/                   # namespace OptiRouter.Routing  (core domain)
│   ├── RouterEngine.cs        # policy-chain orchestrator
│   ├── IRouterPolicy.cs       # policy interface
│   ├── *Policy.cs             # CapabilityFilter, RuleClassifier, SemanticRouter,
│   │                          # LongInput, LatencyAware, BudgetGuard, Failover,
│   │                          # LoadBalance, SessionAffinity
│   ├── RouterDecision.cs / RouterContext.cs  # policy I/O records
│   ├── *Store.cs              # IRequestAuditStore, ICostLedgerStore, ICircuitStateStore
│   │                          # + Sqlite/InMemory impls
│   ├── CostLedger.cs          # budget facade (thread-safe)
│   ├── ModelHealthTracker.cs  # circuit breaker
│   ├── ThompsonSampler.cs / ThompsonStateStore.cs  # MAB
│   ├── *Estimator.cs          # token estimators
│   └── *Engine.cs             # TfIdfSemanticVectorEngine, etc.
├── Endpoints/                 # namespace OptiRouter.Endpoints  (HTTP boundary)
│   ├── ChatCompletionsEndpoint.cs  # /v1/chat/completions
│   ├── ModelsEndpoint.cs          # /api/models
│   ├── ModelsConfigHandler.cs / DashboardHandler.cs  # /api/* management
│   ├── ProxyOrchestrator.cs       # candidate-chain traversal + SendAsync/StreamAsync main loop
│   ├── OutcomeRecorder.cs         # audit/cost/metrics/affinity/Thompson side-effect sink
│   ├── CascadeUpgradeHandler.cs   # Cheap→Strong cascade self-verify
│   ├── FusionRouter.cs            # panel→analyst→outer quality routing
│   ├── RaceOrchestrator.cs        # parallel-first (Fusion-lite) racing
│   ├── FusionAttemptResult.cs     # shared result record (FusionRouter + RaceOrchestrator)
│   ├── ModelClientProvider.cs     # client cache + hot reload
│   └── *Exception.cs              # AllCandidatesFailedException, BudgetExhaustedException
├── Clients/                   # namespace OptiRouter.Clients  (upstream I/O)
│   ├── IModelClient.cs        # client abstraction
│   ├── OpenAICompatibleModelClient.cs
│   ├── ModelClientFactory.cs
│   ├── ChatTypes.cs           # ChatRequest/ChatMessage/response DTOs
│   ├── ModelClientException.cs
│   └── ResponseSizeLimitExceededException.cs  # size-limit signal (LimitBytes field)
├── Health/                    # namespace OptiRouter.Health  (background + checks)
│   ├── AuditRetentionService.cs
│   ├── LatencyStatsAggregatorService.cs
│   ├── MetricsGaugeUpdaterService.cs
│   └── ModelHealthProbeService.cs
├── Metrics/                   # namespace OptiRouter.Metrics
│   └── RouterMetrics.cs       # prometheus-net instruments
├── Concurrency/               # namespace OptiRouter.Concurrency
│   └── ConcurrencyRegistry.cs # partition-based semaphore registry
├── Components/                # Blazor Server (namespace OptiRouter.Components.*)
│   ├── Services/ApiService.cs # dashboard API client
│   └── Pages/, Shared/        # Razor components
├── Pages/                     # Razor Pages (Dashboard, Models)
├── data/                      # SQLite runtime files (gitignored)
└── wwwroot/                   # static assets
```

---

## 3. Contracts — Where Each File Type Goes

| File purpose | Directory | Namespace |
|--------------|-----------|-----------|
| Config options classes + validator + config services | `Configuration/` | `OptiRouter.Configuration` |
| Routing policies, engine, stores, ledger, MAB, estimators | `Routing/` | `OptiRouter.Routing` |
| HTTP handlers, proxy orchestration, HTTP-mapped exceptions | `Endpoints/` | `OptiRouter.Endpoints` |
| Upstream OpenAI-compatible clients + request/response DTOs | `Clients/` | `OptiRouter.Clients` |
| Background services (`BackgroundService`) + health checks | `Health/` | `OptiRouter.Health` |
| Prometheus instruments | `Metrics/` | `OptiRouter.Metrics` |
| Concurrency primitives (semaphore registries) | `Concurrency/` | `OptiRouter.Concurrency` |
| Blazor Server components/services | `Components/` | `OptiRouter.Components.*` |
| Razor Pages (dashboard/models UI) | `Pages/` | `OptiRouter.Pages` |

### Dependency Direction Rule

- `Routing/` depends on `Configuration/` and `Clients/` (DTOs). It does **not** depend on `Endpoints/`.
- `Endpoints/` depends on `Routing/`, `Configuration/`, `Clients/`. It is the composition root for request handling.
- All HTTP-mapped exceptions (`BudgetExhaustedException`, `AllCandidatesFailedException`) live in `Endpoints/` — they are boundary types, not domain.
- `Program.cs` is the only composition root: registers DI, builds the policy chain, wires hot-reload.

---

## 4. Validation & Error Matrix (placement rules)

| Condition | Where it must live |
|-----------|-------------------|
| Config doesn't validate | `Configuration/RouterOptionsValidator.cs` |
| All candidates failed | `Endpoints/AllCandidatesFailedException` (thrown by `ProxyOrchestrator`) |
| Budget exhausted (Reject) | `Endpoints/BudgetExhaustedException` → mapped to 429 |
| Upstream model error | `Clients/ModelClientException` |
| Routing policy behavior | `Routing/*Policy.cs` |

---

## 5. Good/Base/Bad Cases

### Good: Policy-class placement

```csharp
// A new routing policy belongs in Routing/ as OptiRouter.Routing.SomePolicy,
// implements IRouterPolicy, then registered in the Program.cs policy chain.
```

### Base: Interface + dual implementation

```csharp
// Storage abstraction: interface in Routing/, both impls beside it.
//   IRequestAuditStore → SqliteRequestAuditStore + InMemoryRequestAuditStore
//   ICostLedgerStore   → SqliteCostLedgerStore   + InMemoryCostLedgerStore
// DI picks impl by Budget.UsePersistentStore (Program.cs).
```

### Bad: Placing an HTTP-mapped exception in the domain layer

```csharp
// BudgetExhaustedException in Routing/ (domain) instead of Endpoints/
// Result: domain layer now knows about HTTP semantics; Endpoints can't
// cleanly own status mapping. Boundary exceptions belong at the boundary.
```

---

## 6. Tests Required

- A new policy → unit test in `tests/OptiRouter.Tests/Routing/<Policy>Tests.cs`
- A new endpoint → `tests/OptiRouter.Tests/Endpoints/<Endpoint>Tests.cs`
- A new client → `tests/OptiRouter.Tests/Clients/<Client>Tests.cs`
- E2E flow → `tests/OptiRouter.Tests/Smoke/EndToEndSmokeTests.cs` (WireMock)
- Config validation → `tests/OptiRouter.Tests/RouterOptionsValidatorTests.cs`
- Test folder mirrors `src` folder names; helper in `tests/OptiRouter.Tests/Routing/TestHelpers.cs`

---

## 7. Wrong vs Correct

### Wrong: Flat namespace / single folder

```csharp
// All files in src/OptiRouter with namespace OptiRouter.*
// Result: no separation of HTTP boundary vs domain vs config; Policy and
// Endpoint concepts blur; dependency direction unenforceable.
```

### Correct: Folder-per-concern with matching namespace

```csharp
// namespace OptiRouter.Configuration → Configuration/ folder
// namespace OptiRouter.Routing      → Routing/ folder
// namespace OptiRouter.Endpoints    → Endpoints/ folder
// namespace OptiRouter.Clients      → Clients/ folder
// File-scoped namespace matches the folder exactly; new files go in the
// folder matching their layer role.
```

---

## Design Decisions

### Decision: Single project, no backend split

**Context**: Could split into separate `OptiRouter.Domain` / `OptiRouter.Infrastructure` projects.

**Decision**: Single `OptiRouter` web project. Folder-per-concern + namespaces provide enough separation for this scale; the dependency-direction rule is enforced by convention rather than project boundaries. Split projects only if the codebase grows past ~one folder of policies.

### Decision: Exceptions at the boundary

**Context**: `BudgetExhaustedException` and `AllCandidatesFailedException` are HTTP-semantic.

**Decision**: They live in `Endpoints/` because they exist to be mapped to HTTP status codes. Domain (`Routing/`) signals via `RouterDecision.BudgetExhausted` flag, not exceptions — keeping HTTP concerns out of the domain layer.

### Decision: Interface + dual storage impl

**Context**: Tests need in-memory stores; production needs SQLite persistence.

**Decision**: Each store interface (`IRequestAuditStore`, `ICostLedgerStore`) has a `Sqlite*` (prod) and `InMemory*` (test/default) implementation, selected by config in `Program.cs`. This gives test parity without a mocking framework for storage.

### Decision: ProxyOrchestrator split by strategy, not by layer

**Context**: `ProxyOrchestrator.cs` grew to 1307 lines mixing HTTP proxy passthrough, SSE streaming, Race parallel-racing, MoA fusion routing, cascade verification, and fallback retry loops.

**Decision**: Split into 5 collaborating classes, all in `Endpoints/` (same layer — they are request-handling strategies, not domain):
- `OutcomeRecorder` — the 5 side-effect sinks (audit/cost/metrics/affinity/Thompson). Injected into all other orchestrator components; they call `_recorder.RecordXxx(...)` instead of owning the side effects.
- `CascadeUpgradeHandler` / `FusionRouter` / `RaceOrchestrator` — each owns one strategy (`TryUpgradeAsync` / `ExecuteAsync`). `ProxyOrchestrator` delegates via `_cascadeHandler` / `_fusionRouter` / `_raceOrchestrator`.
- `FusionAttemptResult` — shared result record (promoted from nested private).
- `ProxyOrchestrator` shrunk to ~540 lines: candidate-chain traversal + `SendAsync`/`StreamAsync` main loops + probe-slot settlement.

**Extraction rule**: method bodies are moved verbatim; only `this`-references become `_recorder`/`_clientProvider`/etc. The three-state machine (`probeResolved`/`streamFaulted`/`hasFirstLine`) in `StreamAsync` is deliberately left in `ProxyOrchestrator` — it is too coupled to the yield/finally structure to extract safely; a future `SseStreamForwarder` would own it.

**DI wiring**: all 5 are `AddSingleton` in `Program.cs`. `ProxyOrchestrator`'s constructor takes the other 4 as params (DI auto-injects). `OutcomeRecorder` is constructed via factory lambda (needs 7 deps).