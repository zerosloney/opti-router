# Directory Structure

> Frontend (Blazor Server) component and page layout under `src/OptiRouter/`.

---

## 1. Scope / Trigger

- Frontend is a **Blazor Server** app embedded in the same `src/OptiRouter` project as the backend — no separate frontend project.
- Razor components live under `Components/`, Razor Pages under `Pages/`.
- JS interop (Canvas 2D chart) under `wwwroot/js/`, styles under `wwwroot/css/`.

---

## 2. Signatures / Layout

```
src/OptiRouter/
├── Components/                        # Blazor Server components
│   ├── App.razor                      # Root component (<Router>)
│   ├── _Imports.razor                 # Global @using directives
│   ├── Shared/
│   │   └── MainLayout.razor           # Shell layout (header, nav, @Body)
│   ├── Pages/
│   │   ├── Dashboard/
│   │   │   └── Dashboard.razor        # @page "/dashboard" — monitoring
│   │   └── Models/
│   │       └── Models.razor           # @page "/models" — CRUD config
│   └── Services/
│       └── ApiService.cs             # Typed HttpClient for admin API
├── Pages/                             # Razor Pages (host Blazor components)
│   ├── Dashboard/
│   │   └── _Host.cshtml               # Blazor Server host for dashboard
│   └── Models/
│       └── _Host.cshtml               # Blazor Server host for models
│   └── _ViewImports.cshtml            # Shared Razor Page imports
├── wwwroot/
│   ├── css/
│   │   └── blazor.css                 # All component styles (single file)
│   └── js/
│       └── blazor.js                  # JS interop (drawTrendChart)
```

### Namespace Conventions

| Folder | Namespace | Contents |
|--------|-----------|----------|
| `Components/Pages/Dashboard/` | `OptiRouter.Components.Pages.Dashboard` | Dashboard component |
| `Components/Pages/Models/` | `OptiRouter.Components.Pages.Models` | Models CRUD component |
| `Components/Shared/` | `OptiRouter.Components.Shared` | Layout, shared components |
| `Components/Services/` | `OptiRouter.Components.Services` | `ApiService` (HttpClient wrapper) |

---

## 3. Contracts

### Router Configuration (Program.cs)

```csharp
// Blazor Server registration:
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpClient<ApiService>();

// _Host.cshtml loads Blazor via:
// <component type="typeof(App)" render-mode="ServerPrerendered" />
// Requires AddRazorPages for PersistentComponentState (AntiforgeryStateProvider).
```

### File per Component

- Each page is a single `.razor` file with `@code { ... }` block — no code-behind `.razor.cs` files.
- `@implements IDisposable` for timer cleanup (polling, toast auto-dismiss).
- `@inject ApiService Api` / `@inject IJSRuntime JS` / `@inject NavigationManager Nav`.

---

## 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| `Dashboard.razor` Timer callback exception | Caught, silent — polling failure does not break the page |
| `Models.razor` Toast timer after circuit disposed | `InvokeAsync` catches silently |
| Blazor pre-render (`OnAfterRenderAsync` before first render) | `_firstRendered` guard prevents JS interop calls on nonexistent canvas |
| `ApiService.GetMetricsAsync()` returns null | Null-coalescing to `?? 0` / `?? new()` in every binding |
| `ApiService.GetModelsAsync()` returns null | Returns `new List<ModelDto>()` |
| `ApiService.GetTrendsAsync()` returns null | Returns `new List<DailySpend>()` |

---

## 5. Good/Base/Bad Cases

### Good: Timer-based polling with safe teardown

```csharp
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
// Dispose pattern:
public void Dispose() => _refreshTimer?.Dispose();
```

### Base: DTOs as nested records in ApiService

```csharp
public record DashboardMetrics(SystemInfo System, List<ModelInfo> Models);
public record AuditItem(DateTime Timestamp, string Model, ...);
```

### Bad: fire-and-forget `Task.Delay` for polling

```csharp
// In a loop inside OnInitializedAsync:
while (true) { await Task.Delay(2000); await RefreshMetrics(); }
// Result: cannot be cancelled/disposed cleanly, continues after circuit tore down.
// Use Timer instead — it has a Dispose handle.
```

---

## 6. Tests Required

- `ApiService` — key extraction from URL, URL construction with `?key=`, null handling
- `Dashboard.razor` — (optional, Blazor Server component tests via bUnit)
- `Models.razor` — form validation, add/edit/delete flow

---

## 7. Wrong vs Correct

### Wrong: EventCallback for polling without IDisposable

```csharp
// In OnInitializedAsync: _ = Task.Run(async () => { while (true) { ... await Task.Delay(2000); } })();
// Result: circuit disposed but loop continues — unobserved task + unnecessary polling.
```

### Correct: Timer + IDisposable

```csharp
_refreshTimer = new Timer(_ => _ = OnTimerTick(), null, 2000, 2000);
public void Dispose() => _refreshTimer?.Dispose();
```

---

## Design Decisions

### Decision: Single-project Blazor Server (no separate frontend)

**Context**: Could split into a separate SPA or Blazor WASM project.

**Decision**: Embedded Blazor Server in the same `Microsoft.NET.Sdk.Web` project. The frontend is an admin dashboard co-located with the proxy — no separate deployment, no CORS, no auth token refresh. The `/dashboard` and `/models` routes share the same auth (ProxyApiKey/AdminApiKey) as the proxy API.

### Decision: No JavaScript framework / charting library

**Context**: Trend chart could use Chart.js, D3, or a Blazor chart component.

**Decision**: Raw Canvas 2D JS interop (`drawTrendChart`). One-off function, no dependency. The chart is simple (line chart with gradient fill); a library would add weight for marginal benefit.