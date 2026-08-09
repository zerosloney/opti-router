# Quality Guidelines

> Frontend code quality standards for Blazor Server components.

---

## Forbidden Patterns

| Pattern | Why | Instead |
|---------|-----|---------|
| `async void` in Timer callbacks | Unobserved exception crashes the process | `async Task` + try/catch wrapper |
| `Task.Delay` loop for polling | Cannot be cancelled cleanly; continues after circuit disposal | `Timer` + `IDisposable` |
| `@bind` on a record type | Records have `{ get; init; }` — no setter, `@bind` fails | Mutable form class (`class`, not `record`) |
| JS interop in `OnInitializedAsync` | Canvas DOM doesn't exist yet | `OnAfterRenderAsync(bool firstRender)` guard |
| `string` interpolation for API URLs | Easy to miss query separator (`?` vs `&`) | `ApiService.Url()` helper method |
| `double` for monetary values | Floating-point rounding errors | `decimal` for cost, price, budget |

---

## Required Patterns

### IDisposable for Timers

```csharp
@implements IDisposable
// ...
public void Dispose() => _refreshTimer?.Dispose();
```

### Null-Safe Bindings

```razor
@((Metrics?.System.Budget.DailySpend ?? 0).ToString("F6"))
@(m.Tags?.Any() == true ? string.Join(", ", m.Tags) : "-")
```

### Guarded JS Interop

```csharp
private bool _firstRendered;
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender) { _firstRendered = true; if (Trends.Count > 0) await DrawChart(); }
}
private async Task DrawChart()
{
    if (!_firstRendered) return;
    try { await JS.InvokeVoidAsync("drawTrendChart", ...); }
    catch { /* chart init may be delayed */ }
}
```

### Try/Catch in Timer Callbacks

```csharp
private async Task OnTimerTick()
{
    try { await RefreshMetrics(); await InvokeAsync(StateHasChanged); }
    catch { /* silent — Timer exceptions must not escape */ }
}
```

### DTOs as Nested Records

```csharp
// All DTOs in ApiService.cs as nested records.
public record DashboardMetrics(SystemInfo System, List<ModelInfo> Models);
```

---

## Testing Requirements

### What to Test

| Layer | Test Coverage |
|-------|---------------|
| `ApiService` | URL construction, key extraction, null-coalescing |
| Component mount | (Optional via bUnit) Initial render, timer lifecycle |
| Form validation | Empty fields, boundary values, error display |

### Priority

- Integration tests through `ApiService` (HttpClient with mock handler) — highest value
- bUnit component tests — optional (no existing test infrastructure for Blazor)
- JS interop — not tested (Canvas 2D chart; visual, not logic)

---

## Code Review Checklist

- [ ] `@implements IDisposable` when `Timer` is used
- [ ] Timer callback wrapped in try/catch
- [ ] JS interop guarded by `OnAfterRenderAsync(firstRender)` check
- [ ] Null-coalescing on all API response bindings
- [ ] Money values use `decimal`, not `double`
- [ ] DTOs are `record` types, not mutable classes
- [ ] `@bind` targets mutable properties (not record init-only props)
- [ ] `@bind:event="oninput"` for text inputs (realtime, not on blur)
- [ ] Exception in `InvokeAsync` is caught (circuit may be disposed)
- [ ] No `?key=` value logged or exposed in error messages