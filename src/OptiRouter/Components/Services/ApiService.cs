using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using OptiRouter.Configuration;

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
    {
        if (string.IsNullOrEmpty(_key))
            return path;

        char separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{separator}key={Uri.EscapeDataString(_key)}";
    }

    // ── Dashboard ──────────────────────────────────────────────────

    public Task<DashboardMetrics?> GetMetricsAsync()
        => _http.GetFromJsonAsync<DashboardMetrics>(Url("/api/dashboard/metrics"));

    public async Task<List<DailySpend>> GetTrendsAsync(int days = 7)
    {
        var result = await _http.GetFromJsonAsync<List<DailySpend>>(Url($"/api/dashboard/trends?days={days}"));
        return result ?? new List<DailySpend>();
    }

    public async Task<AuditPage> GetAuditLogAsync(int limit = 50, int offset = 0, string? model = null, string? tier = null, string? status = null, long? minLatency = null)
    {
        var url = $"/api/dashboard/requests?limit={limit}&offset={offset}";
        if (!string.IsNullOrEmpty(model))
            url += $"&model={Uri.EscapeDataString(model)}";
        if (!string.IsNullOrEmpty(tier))
            url += $"&tier={Uri.EscapeDataString(tier)}";
        if (!string.IsNullOrEmpty(status))
            url += $"&status={Uri.EscapeDataString(status)}";
        if (minLatency.HasValue && minLatency.Value > 0)
            url += $"&minLatency={minLatency.Value}";

        var result = await _http.GetFromJsonAsync<AuditPage>(Url(url));
        return result ?? new AuditPage(new List<AuditItem>(), 0);
    }

    public async Task<AuditItem?> GetAuditDetailAsync(string id)
    {
        try
        {
            return await _http.GetFromJsonAsync<AuditItem>(Url($"/api/dashboard/requests/detail?id={Uri.EscapeDataString(id)}"));
        }
        catch
        {
            return null;
        }
    }

    public async Task<SandboxResult?> RunSandboxRouteAsync(string prompt)
    {
        using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/sandbox/route"), new { prompt });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<SandboxResult>();
        return null;
    }

    public async Task<EvalReportDto?> RunEvalBenchmarkAsync()
    {
        using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/eval/run"), new { });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<EvalReportDto>();
        return null;
    }

    public Task<SystemConfigDto?> GetSystemConfigAsync()
        => _http.GetFromJsonAsync<SystemConfigDto>(Url("/api/dashboard/config"));

    public async Task<bool> UpdateSystemConfigAsync(UpdateSystemConfigRequest req)
    {
        using var resp = await _http.PutAsJsonAsync(Url("/api/dashboard/config"), req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> OverrideCircuitStateAsync(string modelName, string targetState)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/api/dashboard/circuits/{Uri.EscapeDataString(modelName)}/override"), new { targetState });
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<ClientKeyDto>> GetClientKeysAsync()
    {
        var result = await _http.GetFromJsonAsync<List<ClientKeyDto>>(Url("/api/dashboard/keys"));
        return result ?? new List<ClientKeyDto>();
    }

    public async Task<CreatedClientKeyDto?> CreateClientKeyAsync(string tenantName, decimal dailyBudgetUsd, int maxQps)
    {
        using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/keys"), new { tenantName, dailyBudgetUsd, maxQps });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<CreatedClientKeyDto>();
        return null;
    }

    public async Task<bool> UpdateClientKeyAsync(string key, bool enabled, decimal dailyBudgetUsd, int maxQps)
    {
        using var resp = await _http.PutAsJsonAsync(Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"), new { enabled, dailyBudgetUsd, maxQps });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteClientKeyAsync(string key)
    {
        using var resp = await _http.DeleteAsync(Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"));
        return resp.IsSuccessStatusCode;
    }

    // ── Models ────────────────────────────────────────────────────

    public async Task<List<ModelDto>> GetModelsAsync()
    {
        var result = await _http.GetFromJsonAsync<List<ModelDto>>(Url("/api/models"));
        return result ?? new List<ModelDto>();
    }

    public async Task<bool> CreateModelAsync(CreateModelRequest req)
    {
        using var resp = await _http.PostAsJsonAsync(Url("/api/models"), req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateModelAsync(string name, UpdateModelRequest req)
    {
        using var resp = await _http.PutAsJsonAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"), req);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteModelAsync(string name)
    {
        using var resp = await _http.DeleteAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"));
        return resp.IsSuccessStatusCode;
    }

    public async Task<ModelTestResultDto?> TestModelConnectionAsync(string name)
    {
        using var resp = await _http.PostAsJsonAsync(Url($"/api/models/{Uri.EscapeDataString(name)}/test"), new { });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<ModelTestResultDto>();
        return new ModelTestResultDto(false, 0, "HTTP Request Failed", null);
    }

    // ── DTOs ─────────────────────────────────────────────────────

    public record DashboardMetrics(
        SystemInfo System,
        List<ModelInfo> Models);

    public record SystemInfo(
        DateTime Time,
        [property: JsonPropertyName("routingPolicy")]
        RoutingPolicyInfo Routing,
        BudgetInfo Budget,
        double Qps,
        int TotalRequests,
        long TotalTokens,
        double AvgLatencyMs,
        List<AlertInfo> Alerts,
        double? AvgTtftMs = null,
        long CachedInputTokens = 0,
        long CacheWriteInputTokens = 0,
        RoiInfo? Roi = null,
        SecurityInfo? Security = null,
        List<RecentRequestItem>? RecentRequests = null,
        List<DagGroupInfo>? DagTraces = null);

    public record RoiInfo(
        double BaselineCostUsd,
        double ActualCostUsd,
        double SavedUsd,
        double SavingRatePercent);

    public record SecurityInfo(
        long PiiProtectedTotal,
        long PhoneProtected,
        long EmailProtected,
        long IdCardProtected,
        long CreditCardProtected,
        long IpProtected,
        bool DataSovereigntyEnabled);

    public record DagGroupInfo(
        string? GroupId,
        double TotalCost,
        double MaxLatencyMs,
        List<DagSpanInfo> Spans);

    public record DagSpanInfo(
        string? RequestId,
        string Model,
        string Role,
        double LatencyMs,
        double? TimeToFirstTokenMs,
        double Cost,
        bool Success);

    public record RecentRequestItem(
        string? RequestId,
        DateTime Timestamp,
        string Model,
        string RoutedTier,
        int PromptTokens,
        int CompletionTokens,
        double LatencyMs,
        double? TimeToFirstTokenMs,
        double Cost,
        bool IsStreaming,
        bool Success,
        string? ErrorMessage,
        string? FusionRole);

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
        ModelTier Tier,
        decimal InputPricePerMillion,
        decimal OutputPricePerMillion,
        int MaxContextTokens,
        bool Enabled,
        List<string> Tags,
        string CircuitState,
        int FailureCount,
        int ActiveProbes,
        double? AvgLatencyMs,
        int LatencySamples,
        string Provider = "",
        string Family = "",
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null);

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
        bool IsEstimated,
        double? TimeToFirstTokenMs = null,
        int CachedInputTokens = 0,
        int CacheWriteInputTokens = 0,
        int UncachedInputTokens = 0,
        bool QuotaLimited = false,
        string? RequestId = null,
        string? TraceId = null,
        string? RoutedTier = null,
        string? RoutingReason = null,
        string? ErrorMessage = null);

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
        bool HasApiKey,
        string Provider = "",
        string Family = "",
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool IsLocalOrPrivate = false);

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
        List<string>? Tags,
        string? Provider = null,
        string? Family = null,
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool? IsLocalOrPrivate = null);

    public record UpdateModelRequest(
        string? BaseUrl,
        string? ApiKey,
        string? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion,
        string? Provider = null,
        string? Family = null,
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool? IsLocalOrPrivate = null);

    public record ModelTestResultDto(
        bool Success,
        long LatencyMs,
        string? Message,
        string? Error);

    public record SandboxResult(
        string TargetTier,
        List<SandboxReason> Reasons,
        int EstimatedTokens,
        List<string> CandidateModels);

    public record SandboxReason(string PolicyName, string Message);

    public record EvalReportDto(
        string BatchId,
        DateTimeOffset Timestamp,
        int TotalCases,
        int PassedCases,
        double AccuracyRate,
        double AvgLatencyMs,
        int TotalTokens,
        List<EvalItemDto> Results);

    public record EvalItemDto(
        OptiRouter.Routing.EvalTestCase TestCase,
        string ActualAnswer,
        double SimilarityScore,
        bool Passed,
        long LatencyMs,
        int PromptTokens,
        int CompletionTokens,
        string? ErrorMessage);

    public record SystemConfigDto(
        RoutingConfigDto Routing,
        BudgetConfigDto Budget);

    public record RoutingConfigDto(
        bool EnableFailover,
        bool EnableBudgetGuard,
        bool EnableRuleClassifier,
        bool EnableLatencyAware,
        bool EnableSemanticRouter,
        bool EnablePiiAnonymization,
        bool EnableDataSovereignty,
        bool EnableJsonAstAutoRepair,
        bool EnableFusionRouter);

    public record BudgetConfigDto(
        decimal DailyBudgetUsd,
        string EnforceOnExhausted);

    public record UpdateSystemConfigRequest(
        bool? EnableFailover,
        bool? EnableBudgetGuard,
        bool? EnableRuleClassifier,
        bool? EnableLatencyAware,
        bool? EnableSemanticRouter,
        bool? EnablePiiAnonymization,
        bool? EnableDataSovereignty,
        bool? EnableJsonAstAutoRepair,
        bool? EnableFusionRouter,
        decimal? DailyBudgetUsd,
        string? EnforceOnExhausted);

    public record ClientKeyDto(
        string KeyId,
        string KeyPrefix,
        string TenantName,
        decimal DailyBudgetUsd,
        decimal DailySpendUsd,
        int MaxQps,
        bool Enabled,
        DateTime CreatedAt);

    /// <summary>创建密钥的一次性响应：明文仅此一次返回，之后只存哈希、不可重取。</summary>
    public record CreatedClientKeyDto(
        string PlaintextKey,
        string KeyId,
        string KeyPrefix,
        string TenantName,
        decimal DailyBudgetUsd,
        int MaxQps,
        bool Enabled,
        DateTime CreatedAt);
}
