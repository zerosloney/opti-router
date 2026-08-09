# Database Guidelines

> SQLite persistence: audit store, cost ledger, and circuit-breaker state.

---

## 1. Scope / Trigger

- Single-file SQLite DB (`data/optirouter-budget.db` by default) shared by **two independent connections**:
  - `SqliteRequestAuditStore` — request audit records (`request_audit` table)
  - `SqliteCostLedgerStore` — cost ledger (`daily_spend`, `session_spend`, `total_spend`, `daily_spend_history`) + circuit breaker (`model_circuits`)
- In-memory implementations (`InMemoryRequestAuditStore`, `InMemoryCostLedgerStore`) are used when `Budget.UsePersistentStore=false` or in tests.
- This is plain SQL (Microsoft.Data.Sqlite), **no ORM/EFFramework**. No migrations framework — schema is created `IF NOT EXISTS` on construct, missing columns added incrementally.

---

## 2. Signatures

### Store Interfaces

```csharp
public interface IRequestAuditStore : IDisposable
{
    void Append(RequestAuditRecord record);
    IReadOnlyList<RequestAuditRecord> GetRecent(int limit);
    IReadOnlyList<RequestAuditRecord> GetByModel(string modelName, int limit);
    (IReadOnlyList<RequestAuditRecord> Items, int TotalCount) GetByTimeRange(
        DateTime from, DateTime to, int limit, int offset);
    (int Failures, int Total) GetFailureStats(DateTime from, DateTime to);
    int EvictBefore(DateTime cutoff);
    IReadOnlyDictionary<string, (double AverageLatencyMs, int SampleCount)> GetLatencyStatsSince(DateTime since);
}

public interface ICostLedgerStore : ICircuitStateStore, IDisposable
{
    decimal AddDaily(DateTime utcDate, decimal delta);   // returns new total
    decimal AddTotal(decimal delta);
    decimal GetTotal();
    void ResetTotal();
    decimal AddSession(string sessionId, decimal delta); // returns new total
    decimal GetDaily(DateTime utcDate);
    IReadOnlyList<(DateTime Date, decimal Amount)> GetDailyHistory(int days);
    void SnapshotDaily(DateTime utcDate);
    decimal GetSession(string sessionId);
    void ResetDaily();
    void ResetSession(string sessionId);
    int EvictSessionsBefore(DateTime cutoff);
    void ClearAll();
}

public interface ICircuitStateStore
{
    void SaveCircuitState(string modelName, CircuitState state, int failureCount, DateTime cooldownUntil);
    Dictionary<string, (CircuitState State, int FailureCount, DateTime CooldownUntil)> LoadCircuitStates();
}
```

### Audit Record

```csharp
public sealed record RequestAuditRecord(
    DateTime Timestamp,
    string? RequestId,
    string Model,
    int EstimatedInputTokens,
    int PromptTokens,          // actual from upstream, may be 0
    int CompletionTokens,      // actual from upstream, may be 0
    decimal Cost,              // USD
    long LatencyMs,
    string? SessionId,
    string RoutingReason,
    bool Success,
    string? ErrorMessage,
    bool IsStreaming,
    ModelTier RoutedTier = ModelTier.Medium,
    bool CascadeTriggered = false,
    string? UpgradedFrom = null,
    bool IsAdopted = true,          // parallel: only the adopted attempt is true
    string? ParallelGroupId = null, // parallel: shared across one SendAsync group
    bool IsEstimated = false,       // true = proxied cost, not upstream Usage
    string? FusionRole = null,      // "panel" | "analyst" | "outer" | null
    long? TimeToFirstTokenMs = null,
    int CachedInputTokens = 0,
    int CacheWriteInputTokens = 0,
    int UncachedInputTokens = 0,
    bool QuotaLimited = false);
```

---

## 3. Contracts

### Schema (created on construct, `IF NOT EXISTS`)

```sql
-- Cost ledger (SqliteCostLedgerStore)
CREATE TABLE daily_spend (date TEXT PRIMARY KEY, amount REAL NOT NULL DEFAULT 0);
CREATE TABLE session_spend (
    session_id TEXT PRIMARY KEY, amount REAL NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
CREATE TABLE total_spend (id INTEGER PRIMARY KEY CHECK (id = 1), amount REAL NOT NULL DEFAULT 0);
CREATE TABLE daily_spend_history (date TEXT PRIMARY KEY, amount REAL NOT NULL DEFAULT 0);
CREATE TABLE model_circuits (
    model_name TEXT PRIMARY KEY, state TEXT NOT NULL,
    failure_count INTEGER NOT NULL, cooldown_until TEXT NOT NULL);

-- Audit (SqliteRequestAuditStore)
CREATE TABLE request_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL, request_id TEXT NOT NULL, model TEXT NOT NULL,
    estimated_tokens INTEGER NOT NULL, prompt_tokens INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0, cost REAL NOT NULL DEFAULT 0,
    latency_ms INTEGER NOT NULL DEFAULT 0, session_id TEXT,
    routing_reason TEXT NOT NULL, success INTEGER NOT NULL,
    error_message TEXT, is_streaming INTEGER NOT NULL DEFAULT 0);
CREATE INDEX idx_request_audit_timestamp ON request_audit(timestamp);
CREATE INDEX idx_request_audit_model ON request_audit(model);
```

### Config Keys (BudgetOptions)

| Key | Type | Default | Validation |
|-----|------|---------|------------|
| `Budget.UsePersistentStore` | bool | `true` | — |
| `Budget.StorePath` | string | `data/optirouter-budget.db` | non-empty when `UsePersistentStore=true` |
| `Budget.SessionEvictionHours` | int | `24` | `0`/negative disables eviction |

### Connection & Threading Model

```csharp
// Connection string: Default Timeout (seconds) = busy_timeout, so SQLite waits on
// write-lock contention instead of throwing SQLITE_BUSY
new SqliteConnection($"Data Source={path};Default Timeout=15");

// Every store runs these PRAGMAs on construct:
//   PRAGMA journal_mode=WAL;      -- concurrent reads, crash recovery
//   PRAGMA busy_timeout=5000;     -- in-process cross-store write serialization
```

- **One `SqliteConnection` per store + explicit `lock(_lock)` serializes all writes.** SQLite single-writer semantics; write frequency is low (≤2 ledger accumulations + 1 audit row per request), lock is not a bottleneck.
- **Two stores share one file with independent connections** — cross-store write serialization relies on `busy_timeout` (5000ms / Default Timeout).
- All writes use `BeginTransaction()` + `Commit()`. Atomic accumulate-and-return uses `... RETURNING amount;`.
- Timestamps stored as ISO-8601 `"o"` invariant strings (`ToString("o", CultureInfo.InvariantCulture)`), UTC. Dates stored as `yyyy-MM-dd` (via `FormatDate`).

### Incremental Column Migration

```csharp
// SQLite lacks ADD COLUMN IF NOT EXISTS. Detect via PRAGMA table_info then ALTER.
private void EnsureColumn(string columnName, string definition)
{
    // PRAGMA table_info(request_audit) → collect existing column names
    // if !existing.Contains(columnName):
    //   ALTER TABLE request_audit ADD COLUMN {columnName} {definition};
}
// Legacy columns added this way (with backward-compatible defaults):
//   routed_tier TEXT | cascade_triggered INTEGER DEFAULT 0 | upgraded_from TEXT
//   is_adopted INTEGER DEFAULT 1 | parallel_group_id TEXT
//   is_estimated INTEGER DEFAULT 0 | fusion_role TEXT
//   ttft_ms INTEGER | cached_input_tokens INTEGER DEFAULT 0
//   cache_write_input_tokens INTEGER DEFAULT 0 | uncached_input_tokens INTEGER DEFAULT 0
//   quota_limited INTEGER DEFAULT 0
```

---

## 4. Validation & Error Matrix

| Condition | Error / Behavior | Source |
|-----------|-----------------|--------|
| `Budget.UsePersistentStore=true` + `StorePath` whitespace | `ValidateOptionsResult.Fail` | `RouterOptionsValidator` |
| `StorePath` parent dir missing | Directory auto-created via `Directory.CreateDirectory` in DI factory | `Program.cs` |
| `ICostLedgerStore.AddSession` with empty/null `sessionId` | `ArgumentException.ThrowIfNullOrEmpty` | `SqliteCostLedgerStore` |
| `IRequestAuditStore.Append` with null record | `ArgumentNullException.ThrowIfNull` | `SqliteRequestAuditStore` |
| `RequestAuditStore.GetRecent(limit <= 0)` | Returns empty array (no throw) | `SqliteRequestAuditStore` |
| DB write failure (SQLITE_BUSY exceeds timeout) | Exception propagates; `ProxyOrchestrator` catches on audit path and logs, does not break request | `ProxyOrchestrator` |
| `SQLitePCL.Batteries_V2.Init()` not called | `SqliteException` on first connection | `Program.cs` line 18 (must call before any `Microsoft.Data.Sqlite` use) |

---

## 5. Good/Base/Bad Cases

### Good: Atomic daily accumulate-and-return

```csharp
// SQLite UPSERT with RETURNING — returns the new total atomically, no read-then-write race
INSERT INTO daily_spend (date, amount) VALUES (@date, @delta)
ON CONFLICT(date) DO UPDATE SET amount = amount + @delta
RETURNING amount;
// Result: concurrent AddDaily calls never lose an update.
```

### Base: In-memory store for tests

```csharp
var ledger = new CostLedger(); // null store → InMemoryCostLedgerStore
// Or with session eviction disabled:
var ledger = new CostLedger(sessionEvictionHours: null);
// UsePersistentStore=false → InMemory* in DI (restart resets, tests only)
```

### Bad: Using the same `SqliteConnection` across threads without a lock

```csharp
// SqliteConnection is not thread-safe. Concurrent Requests on one connection
// → corruption / SQLITE_BUSY / InvalidOperationException.
// Correct: one connection + lock(_lock) around every operation, or a new
// connection per operation (not done here — lock is the established pattern).
```

---

## 6. Tests Required

### Assertion Points

| Test | Asserts |
|------|---------|
| `CostLedgerStoreTests` | `AddDaily` returns accumulated total; `GetDaily` reflects it; `ClearAll`/`Reset*` behavior |
| Daily rollover | Cross-UTC-day: `Record` on a new day archives old day, resets daily, keeps `Total` |
| Session accounting | `AddSession`/`GetSession` per `sessionId`; `EvictSessionsBefore` removes stale sessions |
| `RequestAuditStoreTests` | `Append`/`GetRecent`/`GetByModel`/`GetByTimeRange`/`GetFailureStats`/`EvictBefore`/`GetLatencyStatsSince` round-trip all `RequestAuditRecord` fields |
| `GetFailureStats` | Returns `(failures, total)` where failure = `success=0`; used by `AlertEngine` (must not materialize all rows) |
| In-memory vs SQLite parity | Same interface behavior across both implementations (existing dual impls) |
| `GetLatencyStatsSince` | Excludes failed/retry requests (would pollute latency distribution) |
| Routing foundation audit fields | Round-trips nullable TTFT, cache hit/write/uncached token counts, and `QuotaLimited` through in-memory and SQLite stores |

### DI Wiring Test

```csharp
// UsePersistentStore=true → Sqlite*; false → InMemory*
// Directory created if missing; shared file path across both stores
```

---

## 7. Wrong vs Correct

### Wrong: `GetByTimeRange(int.MaxValue)` materialization for failure stats

```csharp
// AlertEngine failure-rate check:
var (failures, total) = store.GetByTimeRange(from, to, int.MaxValue, 0);
double rate = failures / total;
// Result: O(N) memory materialization on every alert check; DB grows unbounded
```

### Correct: Single aggregate query

```csharp
var (failures, total) = store.GetFailureStats(from, to);
// SQL: SELECT COUNT(*) FILTER (WHERE success = 0), COUNT(*) FROM request_audit
//   WHERE timestamp BETWEEN @from AND @to;
// O(1) memory, one aggregate query. Added specifically to replace the full scan.
```

### Wrong: Dropping the `EnsureColumn` migration when adding a field

```csharp
// Adding a new RequestAuditRecord field without EnsureColumn:
// Old DBs (created before the field) get SQL errors on INSERT — column doesn't exist.
// Result: upgrade breaks existing deployments.
```

### Correct: Add the column via `EnsureColumn` with a backward-compatible default

```csharp
// In SqliteRequestAuditStore constructor:
EnsureColumn("is_adopted", "INTEGER NOT NULL DEFAULT 1"); // old rows default to adopted=true
EnsureColumn("parallel_group_id", "TEXT");               // nullable, old rows = NULL
EnsureColumn("ttft_ms", "INTEGER");                      // nullable, old rows = unknown
EnsureColumn("cached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
EnsureColumn("cache_write_input_tokens", "INTEGER NOT NULL DEFAULT 0");
EnsureColumn("uncached_input_tokens", "INTEGER NOT NULL DEFAULT 0");
EnsureColumn("quota_limited", "INTEGER NOT NULL DEFAULT 0");
// Update the INSERT statement to include the field.
```

### Wrong: Omitting `SQLitePCL.Batteries_V2.Init()`

```csharp
// Using Microsoft.Data.Sqlite without initializing the native SQLitePCLRaw
// bundle → SqliteException at first connection.
// Correct: call SQLitePCL.Batteries_V2.Init(); once at Program.cs top,
// before any SqliteConnection is created.
```

---

## Design Decisions

### Decision: Shared single-file DB with two connections over one connection

**Context**: Audit and cost ledger could share one `SqliteConnection`, but they are separate services with independent lifetimes.

**Decision**: Two `SqliteConnection` instances (one per store) on the same file, WAL mode + `busy_timeout=5000` for cross-store write serialization. Simpler than centralizing a connection, and the single-file layout makes backup/ops trivial.

### Decision: Plain SQL + `IF NOT EXISTS` + `EnsureColumn` over an ORM/migrations framework

**Context**: Small schema, no need for EF Core or a migration tool.

**Decision**: `CREATE TABLE IF NOT EXISTS` on construct; incremental columns via `EnsureColumn` (PRAGMA table_info + ALTER). Avoids a framework dependency; the `EnsureColumn` pattern is the standing migration mechanism for evolving `RequestAuditRecord`.

### Decision: `decimal` in code, `REAL` in SQLite

**Context**: Money should be `decimal` in C#. SQLite has no decimal type.

**Decision**: Cast `(double)record.Cost` on write, `ToDecimal()` on read. Precision loss is acceptable for USD cost accumulation (6 significant digits); documented in store code.

### Decision: Session eviction on the `Record` path, not a timer

**Context**: `CostLedger` evicts stale sessions lazily to prevent `session_spend` / in-memory dict unbounded growth.

**Decision**: `EvictSessionsBefore` runs during `Record` when `sessionId` is present, throttled by `evictionCheckInterval` (default 60 min) and gated by `SessionEvictionHours` (0/negative disables). No dedicated timer — `intentional-simple`.
