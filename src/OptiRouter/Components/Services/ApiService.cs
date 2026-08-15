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

    public ApiService(HttpClient http, NavigationManager nav, IHttpContextAccessor? httpContextAccessor)
    {
        _http = http;
        // Set base address so relative paths work inside the Blazor circuit.
        if (_http.BaseAddress == null)
            _http.BaseAddress = new Uri(nav.BaseUri);

        // 管理端鉴权走登录会话 Cookie。Blazor Server 的 HttpClient 在服务端 circuit 内执行，
        // 不会自动携带浏览器 cookie——从 circuit 初始请求的 HttpContext 读取并附加到请求头。
        var cookieHeader = httpContextAccessor?.HttpContext?.Request.Headers.Cookie.ToString();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            _http.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        }
    }

    private static string Url(string path) => path;

    // ── Dashboard ──────────────────────────────────────────────────

    public Task<DashboardMetrics?> GetMetricsAsync()
        => _http.GetFromJsonAsync<DashboardMetrics>(Url("/api/dashboard/metrics"));

    public Task<WindowSummary?> GetWindowSummaryAsync(string window)
        => _http.GetFromJsonAsync<WindowSummary>(Url($"/api/dashboard/metrics/summary?window={Uri.EscapeDataString(window)}"));

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

    /// <summary>多时间窗口统计汇总（输入/输出 token、缓存命中率、错误率等）。WindowHours 为 null 表示"全部"窗口。</summary>
    public record WindowSummary(
        string Window,
        int? WindowHours,
        int RetentionHours,
        DateTime? FromUtc,
        DateTime ToUtc,
        int TotalRequests,
        int Failures,
        double ErrorRatePercent,
        long InputTokens,
        long OutputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long UncachedInputTokens,
        double CacheHitRatePercent,
        double AvgLatencyMs,
        double TotalCost);

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
        ModelTier RoutedTier,
        int PromptTokens,
        int CompletionTokens,
        double LatencyMs,
        double? TimeToFirstTokenMs,
        double Cost,
        bool IsStreaming,
        bool Success,
        string? ErrorMessage,
        string? FusionRole,
        string? RequestContent = null);

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
        ModelTier RoutedTier = ModelTier.Medium,
        string? RoutingReason = null,
        string? ErrorMessage = null,
        string? RequestContent = null);

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
