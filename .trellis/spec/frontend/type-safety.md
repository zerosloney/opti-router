# Type Safety

> TypeScript not used — this is Blazor Server (C#). Type safety comes from C# records, nullable reference types, and JsonSerializer.

---

## 1. Scope / Trigger

- All frontend types are C# classes/records in `ApiService.cs` (nested records).
- JSON deserialization uses `System.Text.Json` (no Newtonsoft).
- DTOs are `record` types with `{ get; init; }` properties (immutable after deserialization).

---

## 2. Signatures

### DTO Definition Pattern

```csharp
// All DTOs are nested records inside ApiService, using System.Text.Json.
public record DashboardMetrics(
    SystemInfo System,
    List<ModelInfo> Models);

public record AuditItem(
    DateTime Timestamp,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    double LatencyMs,
    bool Success,
    bool IsStreaming,
    bool IsEstimated,
    double? TimeToFirstTokenMs = null,
    int CachedInputTokens = 0,
    int CacheWriteInputTokens = 0,
    int UncachedInputTokens = 0,
    bool QuotaLimited = false);
```

### JSON Property Naming

```csharp
// PascalCase by default (System.Text.Json default). Snake_case only when backend
// uses a different name:
public record SystemInfo(
    DateTime Time,
    [property: JsonPropertyName("routingPolicy")]
    RoutingPolicyInfo Routing,   // backend JSON key: "routingPolicy"
    ...);
```

### Nullable Reference Types

```csharp
// NRTs enabled project-wide (<Nullable>enable</Nullable> in csproj).
// API responses that may be null are nullable:
private ApiService.DashboardMetrics? Metrics;  // null before first load

// Record fields that may be null from JSON:
public record AlertInfo(string Id, string Level, string Category, string Message, DateTime Timestamp);
// All string fields here are required (non-nullable) — consistent with backend contract.
```

---

## 3. Contracts

### Deserialization Safety

```csharp
// GetFromJsonAsync returns null on non-2xx. Always null-coalesce:
var result = await _http.GetFromJsonAsync<List<ModelDto>>(Url("/api/models"));
return result ?? new List<ModelDto>();

// For nullable fields, use null-conditional access in bindings:
@((Metrics?.System.Budget.DailySpend ?? 0).ToString("F6"))
```

### Mutable Form Classes (not DTOs)

```csharp
// @bind requires mutable properties. Create private form classes:
private class NewModelForm
{
    public string Name { get; set; } = "";
    public string? ApiKey { get; set; }
    public string Tier { get; set; } = "Medium";
    // ...
}
```

### Key Type: decimal vs double

```csharp
// Money amounts use `decimal` (C#) → JSON number → `decimal` (API response).
// Latency uses `double` (C#) → JSON number → `double`.
// Cost logged as `ToString("F6")` (6 decimal places).
// Dashboard displays cost as `$@m.InputPricePerMillion.ToString("F3")` (3 decimal places).
```

---

## 4. Validation & Error Matrix

| Condition | Behavior |
|-----------|----------|
| JSON missing field on non-nullable record property | `JsonException` on deserialization |
| JSON extra field | Ignored (default `JsonSerializer` behavior) |
| `System.Text.Json` snake_case for `routingPolicy` | `[JsonPropertyName("routingPolicy")]` annotation |
| Null JSON response body | `GetFromJsonAsync` returns null → null-coalesce to empty |

---

## 5. Good/Base/Bad Cases

### Good: Record type with positional constructor

```csharp
public record DailySpend(string Date, decimal Amount);
// Immutable, value equality, JSON deserialization works out of the box.
```

### Base: Property annotation for non-default JSON name

```csharp
[property: JsonPropertyName("routingPolicy")]
RoutingPolicyInfo Routing,
```

### Bad: Mutable DTOs

```csharp
public class AuditItem { public string? Model { get; set; } }  // mutable, no value equality
// Records are preferred for DTOs (immutable, structural equality, concise).
```

---

## 6. Tests Required

- DTO deserialization round-trip tests (match backend JSON shape)
- Dashboard/model DTO round trips for `Provider`, `Family`, nullable cache prices, TTFT, cache token counts, and `QuotaLimited`
- `ApiService` URL construction with `?key=` parameter
- Null-coalescing behavior for all API responses

---

## 7. Wrong vs Correct

### Wrong: Using `double` for money

```csharp
public double Cost { get; set; }  // floating-point rounding errors
```

### Correct: Using `decimal` for money

```csharp
public record AuditItem(..., decimal Cost, ...);  // precise decimal arithmetic
```

### Wrong: Mutable DTOs for API responses

```csharp
public class ModelDto { public string? Name { get; set; } }
// No value equality, no immutability guarantees.
```

### Correct: Immutable records for API responses

```csharp
public record ModelDto(string Name, string BaseUrl, ...);
// Immutable, structural equality, concise, JSON-deserializable.
```

### Routing foundation DTO contract

- Model read/create/update DTOs carry `Provider`, `Family`, `CachedInputPricePerMillion`, and `CacheWriteInputPricePerMillion` end to end. Nullable cache prices mean "use ordinary input price"; they must not be coerced to zero by the UI.
- Dashboard system metrics expose nullable average TTFT plus aggregate cache-hit/cache-write tokens. Audit rows expose nullable TTFT, split cache token counts, and `QuotaLimited`.
- New positional record fields that are absent from older JSON payloads require trailing defaults, preserving deserialization compatibility during rolling upgrades.
- Mutable Blazor form fields mirror the DTO nullability. Provider/family default to empty strings; cache prices remain nullable until explicitly entered.
