# State Management

> Blazor Server component-scoped state — no state management library used.

---

## 1. Scope / Trigger

- No state management library (Redux, Fluxor, Blazor-State, etc.) — all state is **component-scoped**.
- Data is fetched from the admin API (`ApiService`) on mount and refreshed via polling.
- State is held in private fields inside `@code { }` blocks.

---

## 2. Signatures

### State Patterns

```csharp
@code {
    // ── Dashboard state ──
    private ApiService.DashboardMetrics? Metrics;       // nullable — initial load state
    private List<ApiService.DailySpend> Trends = new();
    private List<ApiService.AuditItem> AuditItems = new();
    private int LogOffset = 0;
    private int LogLimit = 50;
    private string? LogFilterModel;
    private int AuditTotal = 0;
    private bool _firstRendered;

    // ── Models state ──
    private List<ApiService.ModelDto> ModelList = new();
    private bool ShowAddForm = false;
    private string? EditingName;
    private string? ErrorMsg;
    private string? ToastMsg;
    private bool ToastIsError = false;
    private NewModelForm NewModel = new();
    private EditModelForm EditModel = new();
}
```

### Derived State (computed on render)

```csharp
private double BudgetPercent =>
    (Metrics?.System.Budget.DailyBudgetUsd ?? 0) > 0
        ? (double)(Metrics?.System.Budget.DailySpend ?? 0) / (double)Metrics!.System.Budget.DailyBudgetUsd * 100
        : 0;

private List<ApiService.AlertInfo> Alerts => Metrics?.System.Alerts ?? new();
```

---

## 3. Contracts

### State Categories

| Category | Storage | Refresh mechanism |
|----------|---------|-------------------|
| Dashboard metrics | `ApiService.DashboardMetrics? Metrics` | Timer polling every 2s |
| Daily spend trends | `List<DailySpend> Trends` | On mount + on trend-days toggle |
| Audit log | `List<AuditItem> AuditItems` + pagination | On mount + on load/paginate |
| Model list | `List<ModelDto> ModelList` | On mount + after CRUD operation |
| Form state | Mutable form objects (`NewModel`, `EditModel`) | User input only |
| UI state | `ShowAddForm`, `EditingName`, `ErrorMsg`, `ToastMsg` | User action only |

### Refresh Flow

```csharp
// Dashboard: metrics auto-refresh via Timer, trends + audit on-demand.
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
// OnTimerTick calls RefreshMetrics() + InvokeAsync(StateHasChanged).

// Models: refresh on mount + after create/update/delete.
protected override async Task OnInitializedAsync() => await LoadModels();
// After CRUD success: await LoadModels(); — re-fetches full list.
```

### No Global State

- No shared state between `Dashboard.razor` and `Models.razor` (they are separate pages).
- `MainLayout.razor` is stateless (no injected state, no interactive data).
- `ApiService` is a singleton `HttpClient` wrapper with no cached state.

---

## 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| `Metrics` is null (initial render before first API response) | All bindings use `?? 0` / `?? new()` / `?.` — no NRE |
| `ModelList` is empty | Table shows "暂无模型配置，点击上方...开始添加" |
| Audit API returns empty | Table shows "暂无数据" |
| Polling timer fires before component is disposed | Normal — `Dispose()` stops the timer |
| API call fails during CRUD | Toast error message, state unchanged |

---

## 5. Good/Base/Bad Cases

### Good: Null-safe derived properties

```csharp
private double BudgetPercent =>
    (Metrics?.System.Budget.DailyBudgetUsd ?? 0) > 0
        ? (double)(Metrics?.System.Budget.DailySpend ?? 0) / (double)Metrics!.System.Budget.DailyBudgetUsd * 100
        : 0;
```

### Base: Mutable form objects for @bind

```csharp
// @bind requires mutable properties. DTOs are records (immutable).
// Use private mutable helper classes for form state:
private class NewModelForm { public string Name { get; set; } = ""; ... }
```

### Bad: Mutating DTO records directly

```csharp
// ApiService.ModelDto is a `record` — properties are { get; init; }.
// @bind won't work because there's no setter.
// Use a mutable form class instead.
```

---

## 6. Tests Required

- Dashboard initial load data flow
- Model CRUD cycle (create, display, edit, delete)
- Polling timer lifecycle (start on mount, stop on dispose)

---

## 7. Wrong vs Correct

### Wrong: Spread operator for state update

```csharp
// Not Blazor — this is a React pattern. Blazor uses mutation + StateHasChanged.
Metrics = newMetrics with { ... }; // with-expression for records works, but mutating fields is fine.
```

### Correct: Reassign the field and call StateHasChanged

```csharp
Metrics = await Api.GetMetricsAsync();
StateHasChanged();  // or via InvokeAsync(StateHasChanged) from timer callback
```