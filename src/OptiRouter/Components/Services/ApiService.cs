using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public ApiService(HttpClient http, NavigationManager nav, IHttpContextAccessor? httpContextAccessor,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
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
        => _http.GetFromJsonAsync<DashboardMetrics>(Url("/api/dashboard/metrics"));

    public Task<WindowSummary?> GetWindowSummaryAsync(string window)
        => _http.GetFromJsonAsync<WindowSummary>(Url($"/api/dashboard/metrics/summary?window={Uri.EscapeDataString(window)}"));

    public async Task<List<DailySpend>> GetTrendsAsync(int days = 7)
    {
        var result = await _http.GetFromJsonAsync<List<DailySpend>>(Url($"/api/dashboard/trends?days={days}"));
        return result ?? new List<DailySpend>();
    }

    public async Task<List<LearningStateDto>> GetLearningAsync()
    {
        var result = await _http.GetFromJsonAsync<List<LearningStateDto>>(Url("/api/dashboard/learning"));
        return result ?? new List<LearningStateDto>();
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
        catch (Exception ex)
        {
            // 静默降级保留诊断线索：区分网络失败与反序列化失败
            _logger?.LogDebug(ex, "GetAuditDetailAsync failed for {Id}", id);
            return null;
        }
    }

    /// <summary>运行路由沙盒仿真。返回 (结果, 失败原因)；失败原因为空表示成功。</summary>
    public async Task<(SandboxResult? Result, string? Error)> RunSandboxRouteAsync(string prompt)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/sandbox/route"), new { prompt });
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
    public async Task<(EvalReportDto? Report, string? Error)> RunEvalBenchmarkAsync(List<EvalCaseDto>? cases)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/eval/run"), new EvalRunRequestDto(cases));
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<EvalReportDto>(), null);
            string body = await resp.Content.ReadAsStringAsync();
            return (null, body);
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
            var result = await _http.GetFromJsonAsync<List<EvalReportDto>>(Url("/api/dashboard/eval/batches"));
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
            using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/eval/compare"), new { baselineBatchId, candidateBatchId });
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<PairedEvalDto>(), null);
            string body = await resp.Content.ReadAsStringAsync();
            return (null, body);
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
            var page = await _http.GetFromJsonAsync<QuotaPageDto>(Url("/api/dashboard/state/quota"));
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
            return await _http.GetFromJsonAsync<AffinityPageDto>(Url("/api/dashboard/state/cache-affinity"))
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
            return await _http.GetFromJsonAsync<ResponseCacheStatsDto>(Url("/api/dashboard/state/response-cache"));
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
            return await _http.GetFromJsonAsync<SemanticRoutesDto>(Url("/api/dashboard/semantic-routes"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSemanticRoutesAsync failed");
            return null;
        }
    }

    /// <summary>整表替换语义路由规则（空列表 = 清空）。返回 (是否成功, 失败原因)。</summary>
    public async Task<(bool Ok, string? Error)> UpdateSemanticRoutesAsync(List<SemanticRouteUpsertDto>? routes)
    {
        try
        {
            using var resp = await _http.PutAsJsonAsync(Url("/api/dashboard/semantic-routes"), new UpdateSemanticRoutesRequestDto(routes));
            if (resp.IsSuccessStatusCode)
                return (true, null);
            string body = await resp.Content.ReadAsStringAsync();
            return (false, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<SystemConfigDto?> GetSystemConfigAsync()
        => _http.GetFromJsonAsync<SystemConfigDto>(Url("/api/dashboard/config"));

    /// <summary>三档路由预设（预设名 → 配置项 → 值），供配置页一键填充。</summary>
    public async Task<Dictionary<string, Dictionary<string, JsonElement>>?> GetPresetsAsync()
    {
        return await _http.GetFromJsonAsync<Dictionary<string, Dictionary<string, JsonElement>>>(Url("/api/dashboard/config/presets"));
    }

    /// <summary>返回 (是否成功, 失败原因)。400 校验错误时 Error 含 RouterOptionsValidator 的具体消息。</summary>
    public async Task<(bool Ok, string? Error)> UpdateSystemConfigAsync(UpdateSystemConfigRequest req)
    {
        using var resp = await _http.PutAsJsonAsync(Url("/api/dashboard/config"), req);
        if (resp.IsSuccessStatusCode)
            return (true, null);
        string body = await resp.Content.ReadAsStringAsync();
        return (false, body);
    }

    /// <summary>手动覆盖模型断路器状态。返回 (是否成功, 失败原因)。</summary>
    public async Task<(bool Ok, string? Error)> OverrideCircuitStateAsync(string modelName, string targetState)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url($"/api/dashboard/circuits/{Uri.EscapeDataString(modelName)}/override"), new { targetState });
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
        var result = await _http.GetFromJsonAsync<List<ClientKeyDto>>(Url("/api/dashboard/keys"));
        return result ?? new List<ClientKeyDto>();
    }

    /// <summary>签发租户密钥。返回 (新密钥信息, 失败原因)；成功时 Key 非空，明文仅此一次返回。</summary>
    public async Task<(CreatedClientKeyDto? Key, string? Error)> CreateClientKeyAsync(string tenantName, decimal dailyBudgetUsd, int maxQps)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url("/api/dashboard/keys"), new { tenantName, dailyBudgetUsd, maxQps });
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
            using var resp = await _http.PutAsJsonAsync(Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"), new { enabled, dailyBudgetUsd, maxQps });
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
            using var resp = await _http.DeleteAsync(Url($"/api/dashboard/keys/{Uri.EscapeDataString(key)}"));
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
        var result = await _http.GetFromJsonAsync<List<ModelDto>>(Url("/api/models"));
        return result ?? new List<ModelDto>();
    }

    public async Task<(bool Ok, string? Error)> CreateModelAsync(CreateModelRequest req)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url("/api/models"), req);
            if (resp.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> UpdateModelAsync(string name, UpdateModelRequest req)
    {
        try
        {
            using var resp = await _http.PutAsJsonAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"), req);
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
            using var resp = await _http.DeleteAsync(Url($"/api/models/{Uri.EscapeDataString(name)}"));
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

    /// <summary>Thompson 学习状态快照。Samples 为进程内计数（重启归零）；LastUpdateUtc 为 MinValue 表示从未收到反馈。</summary>
    public record LearningStateDto(
        string Model,
        double Alpha,
        double Beta,
        double Mean,
        int Samples,
        DateTimeOffset LastUpdateUtc);

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
    public record AffinityPageDto(int TotalCount, List<AffinityStateDto> Items);

    public record AffinityStateDto(
        string Fingerprint,
        string ModelName,
        DateTimeOffset RecordedAt,
        DateTimeOffset ExpiresAt);

    /// <summary>响应缓存命中统计（进程内计数，重启归零）。</summary>
    public record ResponseCacheStatsDto(
        long Hits,
        long Misses,
        long Sets,
        int CurrentEntries,
        int MaxEntries,
        double HitRatePercent);

    /// <summary>语义路由规则集：Enabled/阈值为当前配置只读快照，修改走配置台。</summary>
    public record SemanticRoutesDto(bool Enabled, double SimilarityThreshold, List<SemanticRouteDto> Routes = null!);

    public record SemanticRouteDto(string Name, List<string> Phrases, string TargetTier);

    public record UpdateSemanticRoutesRequestDto(List<SemanticRouteUpsertDto>? Routes);

    public record SemanticRouteUpsertDto(string? Name, List<string>? Phrases, string? TargetTier);

    public record SystemConfigDto(
        RoutingConfigDto Routing,
        BudgetConfigDto Budget);

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
        // ⑥ 观测
        public bool EnableDistributedTracing { get; init; }
        public bool AuditStoreRequestContent { get; init; } = true;
    }

    public record BudgetConfigDto(
        decimal DailyBudgetUsd,
        string EnforceOnExhausted);

    /// <summary>配置更新请求。null = 不修改；属性式与后端 DTO 对齐。</summary>
    public sealed record UpdateSystemConfigRequest
    {
        // ① 基础路由
        public bool? EnableRuleClassifier { get; init; }
        public bool? EnableSemanticRouter { get; init; }
        public bool? EnableSessionAffinity { get; init; }
        public bool? EnableLatencyAware { get; init; }
        public bool? EnableLoadBalance { get; init; }
        public bool? EnableKalmanLoadBalance { get; init; }
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
        // ⑥ 观测
        public bool? EnableDistributedTracing { get; init; }
        public bool? AuditStoreRequestContent { get; init; }
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
