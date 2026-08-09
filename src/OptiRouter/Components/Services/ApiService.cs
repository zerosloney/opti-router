using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;

namespace OptiRouter.Components.Services;

/// <summary>
/// Dashboard API 客户端（Blazor Server 用 ?key= 查询参数鉴权，与现有 JS 版一致）。
/// </summary>
public class ApiService
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private string? _key;

    public ApiService(HttpClient http, NavigationManager nav)
    {
        _http = http;
        _nav = nav;
        // Set base address so relative paths work inside the Blazor circuit.
        if (_http.BaseAddress == null)
            _http.BaseAddress = new Uri(_nav.BaseUri);

        // 浏览器鉴权：管理端 API 经 ?key= 查询参数携带（与鉴权中间件一致）。
        // Blazor Server 下 NavigationManager.Uri 是浏览器当前 URL，含 key。
        // 解析一次写入 _key，后续所有请求自动附加。
        _key = ExtractKeyFromUri(nav.Uri);
    }

    /// <summary>从 URL ?key= 查询参数提取鉴权 key；不存在返回 null。</summary>
    private static string? ExtractKeyFromUri(string absoluteUri)
    {
        if (Uri.TryCreate(absoluteUri, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Query))
        {
            // 手解析避免引 System.Web / Microsoft.AspNetCore.WebUtilities 依赖。
            // query 形如 ?key=xxx&a=b；按 & 分段，段内按 = 拆键值。
            ReadOnlySpan<char> q = uri.Query.AsSpan();
            if (q.Length > 0 && q[0] == '?')
                q = q[1..];
            while (!q.IsEmpty)
            {
                int amp = q.IndexOf('&');
                ReadOnlySpan<char> pair = amp < 0 ? q : q[..amp];
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].SequenceEqual("key"))
                {
                    string val = pair[(eq + 1)..].ToString();
                    return string.IsNullOrEmpty(val) ? null : Uri.UnescapeDataString(val);
                }
                if (amp < 0) break;
                q = q[(amp + 1)..];
            }
        }
        return null;
    }

    public void SetKey(string? key) => _key = key;

    private string Url(string path)
        => string.IsNullOrEmpty(_key) ? path : $"{path}?key={Uri.EscapeDataString(_key)}";

    // ── Dashboard ──────────────────────────────────────────────────

    public Task<DashboardMetrics?> GetMetricsAsync()
        => _http.GetFromJsonAsync<DashboardMetrics>(Url("/api/dashboard/metrics"));

    public async Task<List<DailySpend>> GetTrendsAsync(int days = 7)
    {
        var result = await _http.GetFromJsonAsync<List<DailySpend>>(Url($"/api/dashboard/trends?days={days}"));
        return result ?? new List<DailySpend>();
    }

    public async Task<AuditPage> GetAuditLogAsync(int limit = 50, int offset = 0, string? model = null)
    {
        var url = $"/api/dashboard/requests?limit={limit}&offset={offset}";
        if (!string.IsNullOrEmpty(model))
            url += $"&model={Uri.EscapeDataString(model)}";
        var result = await _http.GetFromJsonAsync<AuditPage>(Url(url));
        return result ?? new AuditPage(new List<AuditItem>(), 0);
    }

    // ── Models ────────────────────────────────────────────────────

    public async Task<List<ModelDto>> GetModelsAsync()
    {
        var result = await _http.GetFromJsonAsync<List<ModelDto>>(Url("/api/models"));
        return result ?? new List<ModelDto>();
    }

    public async Task<bool> CreateModelAsync(CreateModelRequest req)
    {
        var resp = await _http.PostAsJsonAsync(Url("/api/models"), req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateModelAsync(string name, UpdateModelRequest req)
    {
        var resp = await _http.PutAsJsonAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"), req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteModelAsync(string name)
    {
        var resp = await _http.DeleteAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"));
        return resp.IsSuccessStatusCode;
    }

    // ── DTOs ─────────────────────────────────────────────────────

    public record DashboardMetrics(
        SystemInfo System,
        List<ModelInfo> Models);

    public record SystemInfo(
        DateTime Time,
        RoutingPolicyInfo Routing,
        BudgetInfo Budget,
        double Qps,
        int TotalRequests,
        long TotalTokens,
        double AvgLatencyMs,
        List<AlertInfo> Alerts);

    public record RoutingPolicyInfo(
        bool EnableFailover,
        bool EnableBudgetGuard,
        bool EnableRuleClassifier,
        bool EnableLatencyAware,
        bool EnableSemanticRouter,
        bool EnableMultiDimensionalRouting,
        bool EnableThompsonSampling);

    public record BudgetInfo(
        decimal DailyBudgetUsd,
        bool UsePersistentStore,
        decimal DailySpend,
        decimal TotalSpend);

    public record AlertInfo(
        string Id,
        string Level,
        string Category,
        string Message,
        DateTime Timestamp);

    public record ModelInfo(
        string Name,
        string BaseUrl,
        string Tier,
        decimal InputPricePerMillion,
        decimal OutputPricePerMillion,
        int MaxContextTokens,
        bool Enabled,
        List<string> Tags,
        string CircuitState,
        int FailureCount,
        int ActiveProbes,
        double? AvgLatencyMs,
        int LatencySamples);

    public record DailySpend(string Date, decimal Amount);

    public record AuditPage(List<AuditItem> Items, int TotalCount);

    public record AuditItem(
        DateTime Timestamp,
        string Model,
        int PromptTokens,
        int CompletionTokens,
        decimal Cost,
        double LatencyMs,
        bool Success,
        bool IsStreaming,
        bool IsEstimated);

    public record ModelDto(
        string Name,
        string BaseUrl,
        string Tier,
        int MaxContextTokens,
        int TimeoutSeconds,
        int MaxRetries,
        bool Enabled,
        decimal InputPricePerMillion,
        decimal OutputPricePerMillion,
        List<string> Tags,
        bool HasApiKey);

    public record CreateModelRequest(
        string Name,
        string BaseUrl,
        string? ApiKey,
        string Tier,
        int MaxContextTokens,
        int TimeoutSeconds,
        int MaxRetries,
        bool Enabled,
        decimal InputPricePerMillion,
        decimal OutputPricePerMillion,
        List<string>? Tags);

    public record UpdateModelRequest(
        string? BaseUrl,
        string? ApiKey,
        string? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion);
}
