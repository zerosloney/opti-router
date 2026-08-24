using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using OptiRouter.Configuration;

namespace OptiRouter.Components.Services;

/// <summary>
/// Dashboard API 客户端（Blazor Server 管理端认证通过登录会话 Cookie）。
/// </summary>
public class ApiService
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // ApiService 是 Scoped（每 circuit 一份实例），Cookie 缓存在实例上即按管理员会话隔离。
    // 管理会话 Cookie 在预渲染/circuit 建立阶段可从 HttpContext 读到；交互阶段 HttpContext 为 null，
    // 回退用构造时捕获的值。
    private readonly string? _capturedCookie;
    private int _redirected;
    public ApiService(HttpClient http, NavigationManager nav,
        IHttpContextAccessor? httpContextAccessor = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _http = http;
        _nav = nav;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        // Set base address so relative paths work inside the Blazor circuit.
        if (_http.BaseAddress == null)
            _http.BaseAddress = new Uri(nav.BaseUri);

        _capturedCookie = httpContextAccessor?.HttpContext?.Request.Headers.Cookie.ToString();
    }

    private static string Url(string path) => path;

    /// <summary>
    /// 统一请求出口：注入当前 circuit 的管理会话 Cookie；401（会话过期/Cookie 丢失）时每 circuit
    /// 最多跳转登录页一次。不能用 DelegatingHandler 实现——HttpClientFactory 的 handler 管道
    /// 跨 circuit 缓存共享，其实例字段会造成多管理员会话间 Cookie/跳转状态串扰。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? jsonBody = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, url);

        string? cookie = _httpContextAccessor?.HttpContext?.Request.Headers.Cookie.ToString();
        if (string.IsNullOrEmpty(cookie))
            cookie = _capturedCookie; // circuit 交互阶段 HttpContext 不可用，回退捕获值。
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

        if (jsonBody is not null)
            request.Content = JsonContent.Create(jsonBody);

        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && Interlocked.CompareExchange(ref _redirected, 1, 0) == 0)
        {
            // 会话过期后并发请求可能同时拿到 401，原子置位保证每 circuit 只整页跳转一次。
            _nav.NavigateTo("/login", forceLoad: true);
        }
        return response;
    }

    private async Task<T?> GetFromJsonAsync<T>(string url, CancellationToken cancellationToken = default)
        where T : class
    {
        using var resp = await SendAsync(HttpMethod.Get, url, cancellationToken: cancellationToken);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    /// <summary>
    /// 从非 2xx 响应提取后端可读错误（Dashboard/Models API 的 {"error":"..."} 信封）；
    /// 解析失败回退状态码文本。变更类方法经此把后端校验错误带回 UI 展示。
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return err.GetString()!;
            }
            return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body;
        }
        catch (JsonException)
        {
            return $"HTTP {(int)resp.StatusCode}";
        }
    }

    // ── Dashboard ──────────────────────────────────────────────────

    public Task<DashboardMetrics?> GetMetricsAsync()
        => GetFromJsonAsync<DashboardMetrics>(Url("/api/dashboard/metrics"));

    public Task<WindowSummary?> GetWindowSummaryAsync(string window)
        => GetFromJsonAsync<WindowSummary>(Url($"/api/dashboard/metrics/summary?window={Uri.EscapeDataString(window)}"));

    public async Task<List<DailySpend>> GetTrendsAsync(int days = 7)
    {
        var result = await GetFromJsonAsync<List<DailySpend>>(Url($"/api/dashboard/trends?days={days}"));
        return result ?? new List<DailySpend>();
    }

    public async Task<List<LearningStateDto>> GetLearningAsync()
    {
        var result = await GetFromJsonAsync<List<LearningStateDto>>(Url("/api/dashboard/learning"));
        return result ?? new List<LearningStateDto>();
    }

    /// <summary>重置 Thompson/Bandit 学习状态为初始先验（含持久化回落）。</summary>
    public async Task<(bool Ok, string? Error)> ResetLearningAsync()
    {
        using var resp = await SendAsync(HttpMethod.Post, Url("/api/dashboard/learning/reset"));
        return resp.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(resp));
    }

    /// <summary>学习状态 CSV 导出 URL（绝对地址）。</summary>
    public string BuildLearningExportUrl() => _nav.BaseUri.TrimEnd('/') + "/api/dashboard/learning/export";

    /// <summary>告警历史（出现/恢复事件，进程内环形缓冲，重启清空）。</summary>
    public async Task<List<AlertEventDto>> GetAlertHistoryAsync()
    {
        try
        {
            var result = await GetFromJsonAsync<List<AlertEventDto>>(Url("/api/dashboard/alerts/history"));
            return result ?? new List<AlertEventDto>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetAlertHistoryAsync failed");
            return new List<AlertEventDto>();
        }
    }

    /// <summary>配置变更审计历史（谁在何时改了哪项配置）。</summary>
    public async Task<List<ConfigChangeDto>> GetConfigChangesAsync(int limit = 50)
    {
        try
        {
            var result = await GetFromJsonAsync<List<ConfigChangeDto>>(Url($"/api/dashboard/config/history?limit={limit}"));
            return result ?? new List<ConfigChangeDto>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetConfigChangesAsync failed");
            return new List<ConfigChangeDto>();
        }
    }

    public async Task<AuditPage> GetAuditLogAsync(int limit = 50, int offset = 0, string? model = null, string? tier = null, string? status = null, long? minLatency = null, string? q = null, string? from = null, string? to = null)
    {
        var url = AppendAuditFilters($"/api/dashboard/requests?limit={limit}&offset={offset}", model, tier, status, minLatency, q, from, to);
        var result = await GetFromJsonAsync<AuditPage>(Url(url));
        return result ?? new AuditPage(new List<AuditItem>(), 0);
    }

    /// <summary>审计日志 CSV 导出 URL（绝对地址；window.open 同源请求自带管理 Cookie）。</summary>
    public string BuildAuditExportUrl(string? model = null, string? tier = null, string? status = null, long? minLatency = null, string? q = null, string? from = null, string? to = null)
        => _nav.BaseUri.TrimEnd('/') + AppendAuditFilters("/api/dashboard/requests/export", model, tier, status, minLatency, q, from, to);

    private static string AppendAuditFilters(string url, string? model, string? tier, string? status, long? minLatency, string? q, string? from, string? to)
    {
        // 列表 URL 已带 ?limit=..，导出 URL 无查询前缀——首个参数用 ? 后续用 &。
        static string Join(string url, string pair) => url.Contains('?') ? url + "&" + pair : url + "?" + pair;
        if (!string.IsNullOrEmpty(model))
            url = Join(url, $"model={Uri.EscapeDataString(model)}");
        if (!string.IsNullOrEmpty(tier))
            url = Join(url, $"tier={Uri.EscapeDataString(tier)}");
        if (!string.IsNullOrEmpty(status))
            url = Join(url, $"status={Uri.EscapeDataString(status)}");
        if (minLatency.HasValue && minLatency.Value > 0)
            url = Join(url, $"minLatency={minLatency.Value}");
        if (!string.IsNullOrEmpty(q))
            url = Join(url, $"q={Uri.EscapeDataString(q)}");
        if (!string.IsNullOrEmpty(from))
            url = Join(url, $"from={Uri.EscapeDataString(from)}");
        if (!string.IsNullOrEmpty(to))
            url = Join(url, $"to={Uri.EscapeDataString(to)}");
        return url;
    }

    public async Task<AuditItem?> GetAuditDetailAsync(string id)
    {
        try
        {
            return await GetFromJsonAsync<AuditItem>(Url($"/api/dashboard/requests/detail?id={Uri.EscapeDataString(id)}"));
        }
        catch (Exception ex)
        {
            // 静默降级保留诊断线索：区分网络失败与反序列化失败
            _logger?.LogDebug(ex, "GetAuditDetailAsync failed for {Id}", id);
            return null;
        }
    }

    /// <summary>审计分析报告（时间窗全量聚合，供策略调优闭环）。fromUtc/toUtc 需为 UTC。</summary>
    public async Task<AuditAnalysisDto?> GetAuditAnalysisAsync(DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            string from = Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            string to = Uri.EscapeDataString(toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            return await GetFromJsonAsync<AuditAnalysisDto>(Url($"/api/dashboard/audit/analysis?from={from}&to={to}"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetAuditAnalysisAsync failed");
            return null;
        }
    }

    public sealed record AuditAnalysisDto(
        DateTime FromUtc, DateTime ToUtc, int TotalRequests,
        AuditAnalysisSummaryDto Summary,
        List<AuditModelStatsDto> ByModel,
        List<AuditProviderStatsDto> ByProvider,
        List<AuditTierStatsDto> ByTier,
        AuditCascadeDto Cascade,
        AuditFusionDto Fusion,
        List<AuditReasonDto> ByReason,
        List<AuditDayDto> DailyTrend);

    public sealed record AuditAnalysisSummaryDto(
        int Successes, int Failures, double SuccessRatePct, double TotalCostUsd,
        long PromptTokens, long CompletionTokens, long CachedInputTokens,
        double AvgLatencyMs, double P50LatencyMs, double P95LatencyMs, double P99LatencyMs, int LatencySamples);

    public sealed record AuditModelStatsDto(
        string Model, int Requests, int Failures, double SuccessRatePct, double CostUsd,
        double AvgLatencyMs, double P95LatencyMs, long PromptTokens, long CompletionTokens, long CachedInputTokens);

    public sealed record AuditProviderStatsDto(
        string Provider, int Requests, int Failures, double SuccessRatePct,
        long PromptTokens, long CachedInputTokens, long CompletionTokens, double CostUsd, int ModelCount);

    public sealed record AuditTierStatsDto(
        string Tier, int Requests, int Failures, double SuccessRatePct, double CostUsd, double CostSharePct);

    public sealed record AuditCascadeDto(int Triggered, double TriggerRatePct, Dictionary<string, int> UpgradedFrom);

    public sealed record AuditFusionDto(int FusionRequests, Dictionary<string, int> ByRole);

    public sealed record AuditReasonDto(string Reason, int Requests, int Failures, double SuccessRatePct);

    public sealed record AuditDayDto(string Day, int Requests, int Successes, double CostUsd);

    /// <summary>运行路由沙盒仿真。返回 (结果, 失败原因)；失败原因为空表示成功。</summary>
    public async Task<(SandboxResult? Result, string? Error)> RunSandboxRouteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,Url("/api/dashboard/sandbox/route"), new { prompt }, cancellationToken);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<SandboxResult>(), null);
            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>运行回归评测（可传自定义题库；空则后端回落内置题库）。返回 (报告, 失败原因)。</summary>
    public async Task<(EvalReportDto? Report, string? Error)> RunEvalBenchmarkAsync(
        List<EvalCaseDto>? cases,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,
                Url("/api/dashboard/eval/run"),
                new EvalRunRequestDto(cases),
                cancellationToken);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<EvalReportDto>(cancellationToken), null);
            return (null, await ReadErrorAsync(resp));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<List<EvalReportDto>> GetEvalBatchesAsync()
    {
        try
        {
            var result = await GetFromJsonAsync<List<EvalReportDto>>(Url("/api/dashboard/eval/batches"));
            return result ?? new List<EvalReportDto>();
        }
        catch (Exception ex)
        {
            // UI 无法区分"空数据"与"加载失败"，至少在服务端留诊断线索
            _logger?.LogWarning(ex, "GetEvalBatchesAsync failed");
            return new List<EvalReportDto>();
        }
    }

    public async Task<(PairedEvalDto? Report, string? Error)> CompareEvalBatchesAsync(string baselineBatchId, string candidateBatchId)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,Url("/api/dashboard/eval/compare"), new { baselineBatchId, candidateBatchId });
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<PairedEvalDto>(), null);
            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<List<QuotaStateDto>> GetQuotaStateAsync()
    {
        try
        {
            var page = await GetFromJsonAsync<QuotaPageDto>(Url("/api/dashboard/state/quota"));
            return page?.Items ?? new List<QuotaStateDto>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetQuotaStateAsync failed");
            return new List<QuotaStateDto>();
        }
    }

    public async Task<AffinityPageDto> GetCacheAffinityAsync()
    {
        try
        {
            return await GetFromJsonAsync<AffinityPageDto>(Url("/api/dashboard/state/cache-affinity"))
                   ?? new AffinityPageDto(0, new List<AffinityStateDto>());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetCacheAffinityAsync failed");
            return new AffinityPageDto(0, new List<AffinityStateDto>());
        }
    }

    public async Task<ResponseCacheStatsDto?> GetResponseCacheStatsAsync()
    {
        try
        {
            return await GetFromJsonAsync<ResponseCacheStatsDto>(Url("/api/dashboard/state/response-cache"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetResponseCacheStatsAsync failed");
            return null;
        }
    }

    public async Task<SemanticRoutesDto?> GetSemanticRoutesAsync()
    {
        try
        {
            return await GetFromJsonAsync<SemanticRoutesDto>(Url("/api/dashboard/semantic-routes"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSemanticRoutesAsync failed");
            return null;
        }
    }

    /// <summary>
    /// 整表替换语义路由规则（空列表 = 清空）。
    /// 返回 (是否成功, 失败原因, 是否版本冲突, 保存后的新版本号)。
    /// </summary>
    public async Task<(bool Ok, string? Error, bool Conflict, string? Version)> UpdateSemanticRoutesAsync(
        List<SemanticRouteUpsertDto>? routes, string? expectedVersion)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Put, Url("/api/dashboard/semantic-routes"),
                new UpdateSemanticRoutesRequestDto(routes, expectedVersion));
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<SemanticRoutesSaveResponse>();
                return (true, null, false, result?.Version);
            }
            return (false, await ReadErrorAsync(resp), resp.StatusCode == System.Net.HttpStatusCode.Conflict, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, false, null);
        }
    }

    public Task<SystemConfigDto?> GetSystemConfigAsync()
        => GetFromJsonAsync<SystemConfigDto>(Url("/api/dashboard/config"));

    /// <summary>三档路由预设（预设名 → 配置项 → 值），供配置页一键填充。</summary>
    public async Task<Dictionary<string, Dictionary<string, JsonElement>>?> GetPresetsAsync()
    {
        return await GetFromJsonAsync<Dictionary<string, Dictionary<string, JsonElement>>>(Url("/api/dashboard/config/presets"));
    }

    /// <summary>返回 (是否成功, 失败原因)。400 校验错误时 Error 含 RouterOptionsValidator 的具体消息。</summary>
    public async Task<(bool Ok, string? Error, string? Version)> UpdateSystemConfigAsync(UpdateSystemConfigRequest req)
    {
        using var resp = await SendAsync(HttpMethod.Put,Url("/api/dashboard/config"), req);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<UpdateSystemConfigResponse>();
            return (true, null, result?.Version);
        }
        return (false, await ReadErrorAsync(resp), null);
    }

    /// <summary>手动覆盖模型断路器状态。返回 (是否成功, 失败原因)。</summary>
    public async Task<(bool Ok, string? Error)> OverrideCircuitStateAsync(string modelName, string targetState)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,Url($"/api/dashboard/circuits/{Uri.EscapeDataString(modelName)}/override"), new { targetState });
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<ClientKeyDto>> GetClientKeysAsync()
    {
        var result = await GetFromJsonAsync<List<ClientKeyDto>>(Url("/api/dashboard/keys"));
        return result ?? new List<ClientKeyDto>();
    }

    /// <summary>租户用量视图（日消费/剩余预算/用量占比/请求数；不含 KeyHash）。</summary>
    public async Task<List<TenantUsageDto>> GetClientKeysUsageAsync()
    {
        var result = await GetFromJsonAsync<List<TenantUsageDto>>(Url("/api/dashboard/keys/usage"));
        return result ?? new List<TenantUsageDto>();
    }

    /// <summary>租户用量 CSV 导出 URL（绝对地址）。</summary>
    public string BuildKeysUsageExportUrl() => _nav.BaseUri.TrimEnd('/') + "/api/dashboard/keys/usage/export";

    /// <summary>签发租户密钥。返回 (新密钥信息, 失败原因)；成功时 Key 非空，明文仅此一次返回。</summary>
    public async Task<(CreatedClientKeyDto? Key, string? Error)> CreateClientKeyAsync(string tenantName, decimal dailyBudgetUsd, int maxQps)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,Url("/api/dashboard/keys"), new { tenantName, dailyBudgetUsd, maxQps });
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<CreatedClientKeyDto>(), null);
            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> UpdateClientKeyAsync(string key, bool enabled, decimal dailyBudgetUsd, int maxQps)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Put,Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"), new { enabled, dailyBudgetUsd, maxQps });
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> DeleteClientKeyAsync(string key)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Delete,Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"));
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Models ────────────────────────────────────────────────────

    public async Task<List<ModelDto>> GetModelsAsync()
    {
        var result = await GetFromJsonAsync<List<ModelDto>>(Url("/api/models"));
        return result ?? new List<ModelDto>();
    }

    public async Task<(bool Ok, string? Error)> CreateModelAsync(CreateModelRequest req)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post,Url("/api/models"), req);
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>拉取上游 provider 的可订阅模型列表。返回 null 表示网关层错误；502/501 等上游错误由服务端抛出并包装在 err。</summary>
    public async Task<(List<DiscoveredModel>? Models, string? Error)> DiscoverModelsAsync(
        string? baseUrl = null, string? apiKey = null, string protocol = "OpenAI", string? modelName = null)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post, Url("/api/models/discover"),
                new { baseUrl, apiKey, protocol, modelName });
            if (resp.IsSuccessStatusCode)
            {
                var items = await resp.Content.ReadFromJsonAsync<List<DiscoveredModel>>().ConfigureAwait(false);
                return (items ?? new List<DiscoveredModel>(), null);
            }
            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> UpdateModelAsync(string name, UpdateModelRequest req)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Put,Url($"/api/models?name={Uri.EscapeDataString(name)}"), req);
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> DeleteModelAsync(string name)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Delete,Url($"/api/models?name={Uri.EscapeDataString(name)}"));
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<ModelTestResultDto?> TestModelConnectionAsync(string name)
    {
        using var resp = await SendAsync(HttpMethod.Post, Url($"/api/models/test?name={Uri.EscapeDataString(name)}"), new { });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<ModelTestResultDto>();
        return new ModelTestResultDto(false, 0, "HTTP Request Failed", null);
    }

    /// <summary>按需获取单个模型的完整 ApiKey（管理员鉴权；列表接口只返回遮蔽预览）。</summary>
    public async Task<(string? Key, string? Error)> RevealModelApiKeyAsync(string name)
    {
        try
        {
            // 用 query 形态：模型名可含 "/"（display id），path 段无法承载。
            using var resp = await SendAsync(HttpMethod.Get, Url($"/api/models/apikey?name={Uri.EscapeDataString(name)}"));
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<RevealApiKeyResponse>();
                return (result?.ApiKey, null);
            }
            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private sealed record RevealApiKeyResponse(string? ApiKey);

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

    /// <summary>Thompson 学习状态快照。Samples 为进程内计数（重启归零）；LastUpdateUtc 为 MinValue 表示从未收到反馈。</summary>
    public record LearningStateDto(
        string Model,
        double Alpha,
        double Beta,
        double Mean,
        int Samples,
        DateTimeOffset LastUpdateUtc);

    public record AuditPage(List<AuditItem> Items, int TotalCount, bool BufferLimited = false);

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
        string? RequestContent = null,
        bool CascadeTriggered = false,
        string? UpgradedFrom = null,
        string? ParallelGroupId = null,
        string? FusionRole = null,
        string? SpanId = null,
        string? ParentSpanId = null,
        double? Reward = null,
        string? EpsilonPromotedModel = null);

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
        string? ApiKeyHint = null,
        string Provider = "",
        string Family = "",
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool IsLocalOrPrivate = false,
        string Id = "",
        double Weight = 1.0);

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
        bool? IsLocalOrPrivate = null,
        string? Id = null,
        double Weight = 1.0);

    /// <summary>上游模型拉取响应（与 ModelsConfigHandler.DiscoveredModel 同结构）。</summary>
    public record DiscoveredModel(string Id, string? Name, string? OwnedBy, System.Text.Json.JsonElement? Raw);

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
        bool? IsLocalOrPrivate = null,
        List<string>? Tags = null,
        string? Id = null,
        double? Weight = null);

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
        List<EvalItemDto> Results,
        decimal TotalCost = 0,
        int QualityPassedCases = 0,
        int LatencyPassedCases = 0);

    public record EvalItemDto(
        OptiRouter.Routing.EvalTestCase TestCase,
        string ActualAnswer,
        double SimilarityScore,
        bool Passed,
        long LatencyMs,
        int PromptTokens,
        int CompletionTokens,
        string? ErrorMessage,
        string? SelectedModel = null,
        decimal Cost = 0,
        bool QualityPassed = false,
        bool LatencyPassed = false);

    public record EvalRunRequestDto(List<EvalCaseDto>? Cases);

    public record EvalCaseDto(
        string? Id,
        string? Question,
        string? ExpectedAnswer,
        string? Category = null,
        long? MaxLatencyThresholdMs = null);

    /// <summary>两个评测批次的成对 A/B 对比（按用例 ID 配对）。</summary>
    public record PairedEvalDto(
        string BaselineBatchId,
        string CandidateBatchId,
        int PairedCases,
        int CandidateWins,
        int CandidateLosses,
        int Ties,
        double PassRateDelta,
        double QualityPassRateDelta,
        double LatencyPassRateDelta,
        double AvgLatencyDeltaMs,
        int TotalTokenDelta,
        decimal TotalCostDelta,
        List<PairedCaseDto> Cases = null!);

    public record PairedCaseDto(
        string TestCaseId,
        bool BaselinePassed,
        bool CandidatePassed,
        double QualityScoreDelta,
        long LatencyDeltaMs,
        int TokenDelta,
        decimal CostDelta);

    /// <summary>上游配额快照（进程本地，重启清空）。</summary>
    public record QuotaPageDto(List<QuotaStateDto> Items);

    public record QuotaStateDto(
        string ModelName,
        long? RequestsRemaining,
        long? TokensRemaining,
        DateTimeOffset? RequestsResetAt,
        DateTimeOffset? TokensResetAt,
        DateTimeOffset? ExhaustedUntil,
        int? LastStatusCode,
        DateTimeOffset ObservedAt,
        bool IsExhausted);

    /// <summary>提示词缓存亲和：SHA-256 指纹 → 上次成功服务的模型。Items 最多 50 条。</summary>
    public record AffinityPageDto(int TotalCount, List<AffinityStateDto> Items, bool Enabled = false);

    public record AffinityStateDto(
        string Fingerprint,
        string ModelName,
        DateTimeOffset RecordedAt,
        DateTimeOffset ExpiresAt);

    /// <summary>响应缓存命中统计（进程内计数，重启归零）。</summary>
    public record ResponseCacheStatsDto(
        bool Enabled,
        long Hits,
        long Misses,
        long Sets,
        int CurrentEntries,
        int MaxEntries,
        double HitRatePercent);

    /// <summary>语义路由规则集：Version 为配置文档当前版本（保存时回传做乐观并发）。Enabled/阈值为当前配置只读快照。</summary>
    public record SemanticRoutesDto(bool Enabled, double SimilarityThreshold, List<SemanticRouteDto> Routes = null!,
        string? Version = null);

    public record SemanticRouteDto(string Name, List<string> Phrases, string TargetTier);

    public record UpdateSemanticRoutesRequestDto(List<SemanticRouteUpsertDto>? Routes, string? ExpectedVersion = null);

    private sealed record SemanticRoutesSaveResponse(string? Version);

    public record SemanticRouteUpsertDto(string? Name, List<string>? Phrases, string? TargetTier);

    public record SystemConfigDto(
        string Version,
        RoutingConfigDto Routing,
        BudgetConfigDto Budget);

    private sealed record UpdateSystemConfigResponse(string Version);

    /// <summary>
    /// 配置读取 DTO。属性式（与后端 GET 字段一一对应；新增字段给默认值保持向后兼容）。
    /// </summary>
    public record RoutingConfigDto
    {
        public string? Preset { get; init; }
        // ① 基础路由
        public string DefaultTier { get; init; } = "Medium";
        public bool EnableRuleClassifier { get; init; } = true;
        public bool EnableSemanticRouter { get; init; } = true;
        public bool EnableSessionAffinity { get; init; } = true;
        public bool EnableLatencyAware { get; init; }
        public bool EnableLoadBalance { get; init; } = true;
        public bool EnableKalmanLoadBalance { get; init; }
        public bool EnableCapabilityFilter { get; init; }
        // ② 可靠性与预算
        public bool EnableFailover { get; init; } = true;
        public int FailoverFailureThreshold { get; init; } = 3;
        public int FailoverCooldownSeconds { get; init; } = 60;
        public int FailoverGlobalTimeoutSeconds { get; init; }
        public bool EnableBudgetGuard { get; init; } = true;
        public bool EnableHealthProbe { get; init; } = true;
        // ③ 学习与优化
        public bool EnableThompsonSampling { get; init; }
        public bool EnableContextualBandit { get; init; }
        public double ExplorationEpsilon { get; init; }
        public long ExplorationStarvedN { get; init; }
        public bool EnableResponseCache { get; init; }
        public int ResponseCacheTtlSeconds { get; init; } = 3600;
        public int ResponseCacheMaxEntries { get; init; } = 1000;
        public bool EnableSemanticCache { get; init; }
        public double SemanticCacheSimilarityThreshold { get; init; } = 0.94;
        public int SemanticCacheTtlMinutes { get; init; } = 60;
        public bool EnableCascadeUpgrade { get; init; }
        public double CascadeUpgradeSampleRate { get; init; } = 0.1;
        public bool EnableRegenerateFeedback { get; init; }
        // ④ 合规与安全
        public bool EnablePiiAnonymization { get; init; }
        public bool EnableDataSovereignty { get; init; }
        public bool EnableContentModeration { get; init; }
        public double ModerationSampleRate { get; init; } = 1.0;
        public double ModerationThreshold { get; init; } = 0.8;
        public bool EnableStreamingComplianceFilter { get; init; }
        public bool EnablePersonaDriftProtection { get; init; } = true;
        public bool EnablePromptCompression { get; init; }
        // ⑤ 高级编排
        public bool EnableFusionRouter { get; init; }
        public string FusionRouterMinComplexity { get; init; } = "Standard";
        public bool EnableFusionMode { get; init; }
        public bool EnableByzantineConsensus { get; init; }
        public bool EnableJsonAstAutoRepair { get; init; } = true;
        public int FusionRouterPanelSize { get; init; } = 3;
        public bool EnableDynamicFusionPanelSize { get; init; }
        public int FusionRouterMinPanelSize { get; init; } = 2;
        public bool EnableFusionDiversity { get; init; }
        public string? FusionRouterAnalystModel { get; init; }
        public string? FusionRouterAnalystPrompt { get; init; }
        public string? FusionRouterOuterModel { get; init; }
        public int FusionRouterMaxOutputTokens { get; init; } = 16000;
        public double FusionRouterTemperature { get; init; }
        public double? FusionRouterPanelTemperature { get; init; }
        public int FusionRouterPanelTimeoutSeconds { get; init; }
        public int FusionMaxParallel { get; init; } = 2;
        public int FusionHedgeDelayMs { get; init; }
        // ⑥ 观测
        public bool EnableDistributedTracing { get; init; }
        public bool AuditStoreRequestContent { get; init; } = false;
        public int AuditRetentionHours { get; init; } = 0;
        public string AlertWebhookUrl { get; init; } = "";
        public int AlertWebhookIntervalSeconds { get; init; } = 30;
    }

    public record BudgetConfigDto(
        decimal DailyBudgetUsd,
        string EnforceOnExhausted);

    /// <summary>配置更新请求。null = 不修改；属性式与后端 DTO 对齐。</summary>
    public sealed record UpdateSystemConfigRequest
    {
        public string? ExpectedVersion { get; init; }

        // ① 基础路由
        public bool? EnableRuleClassifier { get; init; }
        public bool? EnableSemanticRouter { get; init; }
        public bool? EnableSessionAffinity { get; init; }
        public bool? EnableLatencyAware { get; init; }
        public bool? EnableLoadBalance { get; init; }
        public bool? EnableKalmanLoadBalance { get; init; }
        public bool? EnableCapabilityFilter { get; init; }
        public string? DefaultTier { get; init; }
        // ② 可靠性与预算
        public bool? EnableFailover { get; init; }
        public int? FailoverFailureThreshold { get; init; }
        public int? FailoverCooldownSeconds { get; init; }
        public int? FailoverGlobalTimeoutSeconds { get; init; }
        public bool? EnableBudgetGuard { get; init; }
        public bool? EnableHealthProbe { get; init; }
        public decimal? DailyBudgetUsd { get; init; }
        public string? EnforceOnExhausted { get; init; }
        // ③ 学习与优化
        public bool? EnableThompsonSampling { get; init; }
        public bool? EnableContextualBandit { get; init; }
        public double? ExplorationEpsilon { get; init; }
        public long? ExplorationStarvedN { get; init; }
        public bool? EnableResponseCache { get; init; }
        public int? ResponseCacheTtlSeconds { get; init; }
        public int? ResponseCacheMaxEntries { get; init; }
        public bool? EnableSemanticCache { get; init; }
        public double? SemanticCacheSimilarityThreshold { get; init; }
        public int? SemanticCacheTtlMinutes { get; init; }
        public bool? EnableCascadeUpgrade { get; init; }
        public double? CascadeUpgradeSampleRate { get; init; }
        public bool? EnableRegenerateFeedback { get; init; }
        // ④ 合规与安全
        public bool? EnablePiiAnonymization { get; init; }
        public bool? EnableDataSovereignty { get; init; }
        public bool? EnableContentModeration { get; init; }
        public double? ModerationSampleRate { get; init; }
        public double? ModerationThreshold { get; init; }
        public bool? EnableStreamingComplianceFilter { get; init; }
        public bool? EnablePersonaDriftProtection { get; init; }
        public bool? EnablePromptCompression { get; init; }
        // ⑤ 高级编排
        public bool? EnableFusionRouter { get; init; }
        public string? FusionRouterMinComplexity { get; init; }
        public bool? EnableFusionMode { get; init; }
        public bool? EnableByzantineConsensus { get; init; }
        public bool? EnableJsonAstAutoRepair { get; init; }
        public int? FusionRouterPanelSize { get; init; }
        public bool? EnableDynamicFusionPanelSize { get; init; }
        public int? FusionRouterMinPanelSize { get; init; }
        public bool? EnableFusionDiversity { get; init; }
        public string? FusionRouterAnalystModel { get; init; }
        public string? FusionRouterAnalystPrompt { get; init; }
        public string? FusionRouterOuterModel { get; init; }
        public int? FusionRouterMaxOutputTokens { get; init; }
        public double? FusionRouterTemperature { get; init; }
        public double? FusionRouterPanelTemperature { get; init; }
        public int? FusionRouterPanelTimeoutSeconds { get; init; }
        public int? FusionMaxParallel { get; init; }
        public int? FusionHedgeDelayMs { get; init; }
        // ⑥ 观测
        public bool? EnableDistributedTracing { get; init; }
        public bool? AuditStoreRequestContent { get; init; }
        public int? AuditRetentionHours { get; init; }
        public string? AlertWebhookUrl { get; init; }
        public int? AlertWebhookIntervalSeconds { get; init; }
    }

    /// <summary>预设展示项：名称 + 中文标题 + 描述 + 原始字段值（供配置页填充表单）。</summary>
    public record PresetSummaryDto(
        string Name,
        string Title,
        string Description,
        Dictionary<string, JsonElement> Values);

    public record ClientKeyDto(
        string KeyId,
        string KeyPrefix,
        string TenantName,
        decimal DailyBudgetUsd,
        decimal DailySpendUsd,
        int MaxQps,
        bool Enabled,
        DateTime CreatedAt);

    /// <summary>租户实时用量（后端 /keys/usage）：占比 0-100，预算为 0 时为 0。</summary>
    public record TenantUsageDto(
        string KeyId,
        string KeyPrefix,
        string TenantName,
        decimal DailyBudgetUsd,
        decimal DailySpendUsd,
        decimal RemainingBudgetUsd,
        double QuotaUtilization,
        int DailyRequestCount,
        int MaxQps,
        bool Enabled,
        DateTime CreatedAt);

    /// <summary>告警历史事件：EventType 为 alert（出现）/ resolved（恢复）。</summary>
    public record AlertEventDto(
        DateTimeOffset Timestamp,
        string EventType,
        string AlertId,
        string Level,
        string Category,
        string Message);

    /// <summary>配置变更记录：Changes 为 [{key, from, to}]（from/to 为 JSON 值文本，可能为 null）。</summary>
    public record ConfigChangeDto(long Id, string Timestamp, string Actor, JsonElement Changes);

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
