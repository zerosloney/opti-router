# Component Guidelines

> Blazor Server component patterns used in the dashboard.

---

## 1. Scope / Trigger

- Blazor Server (not WASM). Components are server-rendered, UI updates via SignalR.
- Two page components: `Dashboard.razor` (`@page "/dashboard"`) and `Models.razor` (`@page "/models"`).
- Shared layout via `MainLayout.razor` (`@inherits LayoutComponentBase`).
- All components are single-file `.razor` with `@code { ... }`. No code-behind `.razor.cs` files.

---

## 2. Signatures

### Typical Component Structure

```razor
@page "/dashboard"
@implements IDisposable
@inject ApiService Api
@inject IJSRuntime JS

<PageTitle>...</PageTitle>

<div class="page-grid">
    <!-- HTML markup -->
</div>

@code {
    // Fields — mutable state, initialized in OnInitializedAsync
    private ApiService.DashboardMetrics? Metrics;
    private Timer? _refreshTimer;

    protected override async Task OnInitializedAsync()
    {
        await RefreshMetrics();
        _refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
    }

    private async Task OnTimerTick()
    {
        try { await RefreshMetrics(); await InvokeAsync(StateHasChanged); }
        catch { /* silent — polling failure does not break the page */ }
    }

    public void Dispose() => _refreshTimer?.Dispose();
}
```

### Lifecycle Methods Used

| Method | Usage |
|--------|-------|
| `OnInitializedAsync()` | Initial data load, start polling timer |
| `OnAfterRenderAsync(bool firstRender)` | Chart JS interop (only after canvas is real) |
| `Dispose()` (via `IDisposable`) | Dispose polling timers and toast timers |

---

## 3. Contracts

### Component Patterns

| Pattern | Where | Example |
|---------|-------|---------|
| `@implements IDisposable` | Dashboard + Models | Timer cleanup |
| `@inject` for DI | Dashboard + Models | `ApiService`, `IJSRuntime`, `NavigationManager` |
| `@bind` for form inputs | Models | `@bind="NewModel.Name"` / `@bind:event="oninput"` |
| `@onclick` for actions | Both | `@onclick="() => SetTrendDays(7)"` |
| `@if` conditional rendering | Both | `@if (Alerts.Any())` / `@if (ShowAddForm)` |
| `@foreach` for lists | Both | `@foreach (var m in Metrics.Models)` |
| Element reference | Dashboard | `@ref="ChartCanvas"` for JS interop |
| `@code` block | Both | All state, lifecycle, and event handlers |

### Timer Pattern (Polling + Toast)

```csharp
// Timer-based, not Task.Delay — allows Dispose to cancel cleanly.
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
// Toast auto-dismiss:
_toastTimer = new Timer(_ => _ = OnToastTimeout(), null, 3000, Timeout.Infinite);
// Both are disposed in IDisposable.Dispose().
```

### JS Interop Guard

```csharp
// Prerender phase has no real canvas — guard JS calls.
private bool _firstRendered;
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender) { _firstRendered = true; if (Trends.Count > 0) await DrawChart(); }
}
private async Task DrawChart()
{
    if (Trends.Count == 0) return;
    if (!_firstRendered) return;
    try { await JS.InvokeVoidAsync("drawTrendChart", ChartCanvas, Trends); }
    catch { /* chart init may be delayed */ }
}
```

### Auth via `?key=` Query Parameter

```csharp
// Blazor Server runs in the browser's SignalR circuit. The URL bar contains ?key=.
// ApiService extracts it once on construction and appends to all admin API calls.
// See ApiService.ExtractKeyFromUri() for the parsing logic.
```

---

## 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| Models form: empty Name/BaseUrl | Client-side validation, `ErrorMsg = "..."` set, no API call |
| Models form: API call fails | `Toast("...", isError: true)` |
| Dashboard: metrics API returns null | `?? 0` / `?? new()` in every binding |
| Dashboard: polling timer exception | Caught, silent — no crash |
| Models: toast timer after circuit disposed | `InvokeAsync` catches silently |

---

## 5. Good/Base/Bad Cases

### Good: `@bind` for form fields with `<select>`

```razor
<select @bind="NewModel.Tier">
    <option value="Strong">Strong</option>
    <option value="Medium">Medium</option>
    <option value="Cheap">Cheap</option>
</select>
```

### Bad: Using `@bind` for complex objects without diffing

```razor
@bind="NewModel"  <!-- NOT supported — Blazor @bind is per-property -->
```

---

## 6. Tests Required

- `Dashboard.razor` — (optional) mount via bUnit, assert initial render, trigger timer
- `Models.razor` — form validation, add/edit/delete flow
- `MainLayout.razor` — nav link rendering, active class

---

## 7. Wrong vs Correct

### Wrong: Unhandled exception in Timer callback

```csharp
_refreshTimer = new Timer(_ => RefreshMetrics(), null, 2000, 2000);
// If RefreshMetrics throws, the exception is unobserved — process may crash.
```

### Correct: Try/catch in Timer callback

```csharp
private async Task OnTimerTick()
{
    try { await RefreshMetrics(); await InvokeAsync(StateHasChanged); }
    catch { /* silent */ }
}
```

### Wrong: fire-and-forget Task.Delay in OnInitializedAsync

```csharp
_ = Task.Run(async () => { while (true) { await Task.Delay(2000); Refresh(); } });
// Cannot be cancelled Cleanly. Continues after circuit disposal.
```

### Correct: Timer + IDisposable

```csharp
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
public void Dispose() => _refreshTimer?.Dispose();
```

---

## 8. Sliding Glass Drawer & Modal Patterns

### Pattern: Glassmorphism Sliding Detail Drawer
- Clickable rows set `@onclick="() => SelectLogItem(item)"` with `class="hover-row"` and `cursor: pointer`.
- Drawer backdrop fixed overlay (`rgba(0,0,0,0.6)`), drawer container (`#0f172a`, `border-left: 1px solid rgba(56,189,248,0.3)`).
- Critical stopPropagation on inner drawer container: `@onclick:stopPropagation="true"` so clicking inside does not trigger backdrop dismissal.

```razor
@if (SelectedLogItem != null)
{
    <div class="drawer-backdrop" @onclick="() => SelectedLogItem = null" style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.6); z-index: 9999; display: flex; justify-content: flex-end;">
        <div class="drawer-content" @onclick:stopPropagation="true" style="width: 520px; max-width: 90vw; height: 100%; background: #0f172a; padding: 20px; overflow-y: auto;">
            <!-- Content -->
        </div>
    </div>
}
```