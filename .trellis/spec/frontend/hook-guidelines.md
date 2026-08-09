# Hook Guidelines

> Blazor lifecycle methods and component patterns — no hooks library used.

---

## 1. Scope / Trigger

- This project uses **Blazor Server** lifecycle methods (`OnInitializedAsync`, `OnAfterRenderAsync`, `Dispose`) — not React-style hooks.
- "Hook" in this context refers to Blazor's built-in lifecycle methods, their usage conventions, and the `@inject` DI pattern.

---

## 2. Signatures

### Lifecycle Methods Used

| Method | When | Common Use |
|--------|------|------------|
| `OnInitializedAsync()` | Component first render (before DOM) | Load initial data, start polling timers |
| `OnAfterRenderAsync(bool firstRender)` | After DOM is rendered | Canvas JS interop (only on `firstRender`), chart drawing |
| `Dispose()` (via IDisposable) | Component is torn down | Dispose `Timer` instances |

### DI Injection

```razor
@inject ApiService Api
@inject IJSRuntime JS
@inject NavigationManager Nav  // in MainLayout.razor
```

---

## 3. Contracts

### `OnInitializedAsync` Pattern

```csharp
protected override async Task OnInitializedAsync()
{
    await RefreshAll();  // or RefreshMetrics() + LoadLogs()
    // Start polling after initial load:
    _refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
}
```

### `OnAfterRenderAsync` Guard

```csharp
private bool _firstRendered;
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
    {
        _firstRendered = true;
        if (Trends.Count > 0)
            await DrawChart();
    }
}
// DrawChart() bails early if !_firstRendered — prevents JS interop before DOM is ready.
```

### `Dispose` Cleanup

```csharp
public void Dispose()
{
    _refreshTimer?.Dispose();   // polling timer
    _toastTimer?.Dispose();     // toast auto-dismiss timer
}
```

### Timer Callback Pattern

```csharp
private async Task OnTimerTick()
{
    try
    {
        await RefreshMetrics();                           // data fetch
        await InvokeAsync(StateHasChanged);               // re-render on Blazor UI thread
    }
    catch
    {
        // silent — Timer exceptions must not escape (unobserved exception = process crash).
    }
}

private async Task OnToastTimeout()
{
    try
    {
        await InvokeAsync(() => { ToastMsg = null; StateHasChanged(); });
    }
    catch
    {
        // silent — circuit may already be disposed.
    }
}
```

---

## 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| Timer callback throws | Caught, silent — exception must not escape the Timer callback |
| `InvokeAsync` after circuit disposed | Caught, silent — `InvokeAsync` throws `ObjectDisposedException` |
| `OnAfterRenderAsync` JS interop on prerender | `_firstRendered` guard prevents the call |
| Second `OnAfterRenderAsync` call (non-firstRender) | Chart not redrawn — only `firstRender` triggers drawing |

---

## 5. Good/Base/Bad Cases

### Good: Lifecycle separation

```csharp
// OnInitializedAsync: load data, start timers
// OnAfterRenderAsync: JS interop (after DOM ready)
// Dispose: clean up timers
```

### Bad: JS interop in OnInitializedAsync

```csharp
protected override async Task OnInitializedAsync()
{
    await JS.InvokeVoidAsync("drawTrendChart", ...); // canvas DOM doesn't exist yet
}
```

### Bad: Polling without Dispose

```csharp
// OnInitializedAsync starts a Task.Delay loop but has no Dispose().
// Timer continues after component is removed — leaked timer + stale callbacks.
```

---

## 6. Tests Required

- (Optional via bUnit) Verify lifecycle methods fire correctly on mount/unmount
- Verify `Dispose` stops timers (no post-disposal callbacks)

---

## 7. Wrong vs Correct

### Wrong: Async void in Timer callback

```csharp
_refreshTimer = new Timer(async _ => { await RefreshMetrics(); }, null, 2000, 2000);
// async void — exception cannot be caught by caller, crashes process.
```

### Correct: async Task + timer callback wrapper

```csharp
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
private async Task OnTimerTick() { try { ... } catch { } }
```