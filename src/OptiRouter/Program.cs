using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Components.Services;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Health;
using OptiRouter.Routing;
using Prometheus;

// 初始化 SQLitePCLRaw 原生库（使用 bundle_e_sqlite3）。必须在使用 Microsoft.Data.Sqlite 前调用一次。
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB limit
});

// 注册 models-config.json 为配置源（后于 appsettings.json，覆盖 OptiRouter:Models 段）。
// Dashboard 写入该文件后 ModelsConfigService 触发 IConfigurationRoot.Reload()，IOptionsMonitor 派发到 router。
// 首启种子：config.json 不存在时，把 appsettings.json 的 Models 段写入 config.json，
// 使 provider 首次 Load 即覆盖 appsettings 的 index 0..N（消除双源 index 合并残留）。
string modelsConfigPath = Path.Combine(builder.Environment.ContentRootPath, "models-config.json");
if (!File.Exists(modelsConfigPath))
{
    try
    {
        var seeded = builder.Configuration.GetSection("OptiRouter:Models")
            .Get<List<OptiRouter.Configuration.ModelEndpointOptions>>();
        string? parentDir = Path.GetDirectoryName(modelsConfigPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        using var stream = new FileStream(modelsConfigPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        System.Text.Json.JsonSerializer.Serialize(stream,
            seeded ?? new List<OptiRouter.Configuration.ModelEndpointOptions>(),
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            });
    }
    catch (IOException)
    {
        // 并发初始化防御：忽略并发创建竞争
    }
}
builder.Configuration.Sources.Add(new ModelsJsonConfigurationSource
{
    FilePath = modelsConfigPath
});

// Bind and validate RouterOptions on startup.
builder.Services.AddMemoryCache();
builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection("OptiRouter"))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<RouterOptions>>(sp =>
    new RouterOptionsValidator(sp.GetRequiredService<ILogger<RouterOptionsValidator>>()));

// 注册模型客户端工厂。
builder.Services.AddSingleton<ModelClientFactory>();

// 注册模型客户端提供者（生产实现，按模型名缓存 IModelClient）。
// 热更新：内部订阅 IOptionsMonitor.OnChange，BaseUrl/ApiKey/TimeoutSeconds 变化时重建对应客户端，
// 旧客户端保留一段宽限期后释放，不打断在途请求。
builder.Services.AddSingleton<IModelClientProvider>(sp => new ModelClientProvider(
    sp.GetRequiredService<ModelClientFactory>(),
    sp.GetRequiredService<IOptionsMonitor<RouterOptions>>()));

// 成本账本存储：UsePersistentStore=true 用 SQLite（跨重启保留），否则用内存（重启归零）。
// SQLite 文件目录在构建时创建，确保单例构造时路径可写。
builder.Services.AddSingleton<ICostLedgerStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    if (!options.Budget.UsePersistentStore)
    {
        return new InMemoryCostLedgerStore();
    }

    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return new SqliteCostLedgerStore(storePath);
});

// 请求审计存储：与成本账本共享同一持久化策略（SQLite 或内存）。
builder.Services.AddSingleton<IRequestAuditStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    if (!options.Budget.UsePersistentStore)
    {
        return new InMemoryRequestAuditStore();
    }

    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return new SqliteRequestAuditStore(storePath);
});

// t3: 注册成本账本、跨请求模型健康跟踪器（三态断路器）和路由引擎。
builder.Services.AddSingleton<CostLedger>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    var store = sp.GetRequiredService<ICostLedgerStore>();
    return new CostLedger(store, options.Budget.SessionEvictionHours);
});
builder.Services.AddSingleton<ModelHealthTracker>(sp =>
{
    var store = sp.GetRequiredService<ICostLedgerStore>();
    return new ModelHealthTracker(store);
});

// 延迟统计缓存：后台聚合服务写入，路由策略零 I/O 读快照。
builder.Services.AddSingleton<ILatencyStatsProvider, LatencyStatsCache>();

// 告警引擎：检查预算、断路器、失败率等条件。
builder.Services.AddSingleton<AlertEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var auditStore = sp.GetRequiredService<IRequestAuditStore>();
    var routerOptions = sp.GetRequiredService<IOptionsMonitor<RouterOptions>>();
    return new AlertEngine(ledger, healthTracker, auditStore, routerOptions);
});

// Token 估算器：Tiktoken 模式用 SharpToken 真实 BPE 计数（内置词表、离线可用，异常自动回退分桶粗估）；
// Bucket 模式用分桶加权粗估。编码名校验由 RouterOptionsValidator 在启动时完成。
builder.Services.AddSingleton<ITokenEstimator>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    if (options.Routing.TokenEstimation == TokenEstimationMode.Bucket)
        return new BucketTokenEstimator();

    return new TiktokenTokenEstimator(options.Routing.TiktokenEncoding);
});

builder.Services.AddSingleton<ISemanticVectorEngine, TfIdfSemanticVectorEngine>();
builder.Services.AddSingleton<ThompsonStateStore>();

builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var tokenEstimator = sp.GetRequiredService<ITokenEstimator>();
    var vectorEngine = sp.GetRequiredService<ISemanticVectorEngine>();
    var tsStore = sp.GetRequiredService<ThompsonStateStore>();
    // 策略链在请求处理时读取 IOptionsMonitor.CurrentValue（ProxyOrchestrator 注入），
    // Tier/价格等字段 reload 后立即生效；Models 端点连接配置（BaseUrl/ApiKey/Timeout）
    // 缓存于 ModelClientProvider，经 OnChange 热更新重建（见其注册处）。
    var policies = new List<IRouterPolicy>
    {
        new CapabilityFilterPolicy(),
        new RuleClassifierPolicy(),
        new SessionAffinityPolicy(sp.GetRequiredService<IMemoryCache>()),
        new SemanticRouterPolicy(vectorEngine),
        new LongInputPolicy(),
        new LatencyAwarePolicy(sp.GetRequiredService<ILatencyStatsProvider>(), tsStore),
        new BudgetGuardPolicy(ledger),
        new FailoverPolicy(healthTracker),
        new LoadBalancePolicy()
    };
    return new RouterEngine(ledger, policies, tokenEstimator);
});

// t4: 注册降级重试编排器。
builder.Services.AddSingleton<ProxyOrchestrator>();

// Prometheus 指标集合（单例，ProxyOrchestrator 经 DI 注入）。
// 仪表（Counter/Histogram/Gauge）在 RouterMetrics 构造时向 prometheus-net 静态注册表登记，
// 后台 MetricsGaugeUpdaterService 周期刷新花费/断路器 gauge。
builder.Services.AddSingleton<OptiRouter.Metrics.RouterMetrics>();

// 模型配置文件服务（独立 models-config.json，Dashboard 读写，IConfigurationRoot.Reload() 热生效）。
builder.Services.AddSingleton<ModelsConfigService>(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var configRoot = (IConfigurationRoot)sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ModelsConfigService>>();
    return new ModelsConfigService(
        Path.Combine(env.ContentRootPath, "models-config.json"),
        configRoot,
        logger);
});

// 后台定时主动探活：启动预热一轮，随后按 HealthProbeIntervalSeconds 周期对所有启用模型探测，
// 结果上报 ModelHealthTracker（成功累计半开/闭合，失败计熔断）。EnableHealthProbe=false 可关闭。
builder.Services.AddHostedService<ModelHealthProbeService>();

// 后台周期聚合模型延迟统计，写入 ILatencyStatsProvider 供 LatencyAwarePolicy 读。
// 复用 HealthProbeIntervalSeconds 周期，避免引入独立定时器。EnableLatencyAware=false 时不聚合。
builder.Services.AddHostedService<LatencyStatsAggregatorService>();

// 审计保留淘汰：按 AuditRetentionHours 周期 EvictBefore，防止 request_audit 无界增长。
builder.Services.AddHostedService<AuditRetentionService>();

// 指标 gauge 刷新服务：周期同步花费/断路器 gauge（复用探活周期，零独立定时器）。
// EnableMetrics=false 时不影响功能，但 gauge 保持零值。
builder.Services.AddHostedService<MetricsGaugeUpdaterService>();

// 健康检查：验证内部依赖（成本账本 store 连接正常）。
builder.Services.AddHealthChecks()
    .AddCheck<CostLedgerHealthCheck>("cost-ledger", failureStatus: HealthStatus.Unhealthy);

// Blazor Server：组件化 Dashboard + 模型配置 UI。
// _Host.cshtml 是 Razor Page（用 <component render-mode="ServerPrerendered">），
// 需 AddRazorPages 提供 PersistentComponentState 等预渲染服务，否则 AntiforgeryStateProvider 解析失败。
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpClient<ApiService>();

int requestsPerMinute = builder.Configuration.GetValue<int?>("OptiRouter:RequestsPerMinute") ?? 60;
if (requestsPerMinute <= 0)
    throw new InvalidOperationException("OptiRouter:RequestsPerMinute must be greater than zero.");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
            return RateLimitPartition.GetNoLimiter("public");

        // 每请求从已合并的 IConfiguration 读阈值（含 WebApplicationFactory 经 ConfigureAppConfiguration 注入的值）。
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        bool trustProxy = config.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false;
        string partitionKey = ResolvePartitionKey(context, trustProxy);

        // 注意：FixedWindowRateLimiter 的 PermitLimit 在分区首次创建时定型，运行时改配置仅对新建分区生效，
        // 既有分区沿用创建时的值——变更全局生效需重启进程。这是 ASP.NET 限流器的固有约束，非可热更。
        int limit = config.GetValue<int?>("OptiRouter:RequestsPerMinute") ?? requestsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

// 配置热重载时清理 Thompson 采样状态：剔除已删除/改名的模型条目，防 _states 无界泄漏。
// OnChange 在 models-config.json 写入触发 IConfigurationRoot.Reload 后派发。
var tsStoreForReload = app.Services.GetRequiredService<ThompsonStateStore>();
var routerOptionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>();
routerOptionsMonitor.OnChange(options =>
{
    tsStoreForReload.Retain(options.Models.Select(m => m.Name));
});

// Serve the Blazor boot script and the dashboard's CSS/JavaScript before the
// authentication middleware. Framework asset requests cannot carry the admin key.
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue("X-Request-Id", out var requestId) || string.IsNullOrEmpty(requestId))
    {
        requestId = Guid.NewGuid().ToString("N");
    }
    context.Response.Headers["X-Request-Id"] = requestId;
    context.Items["RequestId"] = requestId.ToString();
    await next(context).ConfigureAwait(false);
});

static bool IsProtectedPath(PathString path) =>
    path.StartsWithSegments("/v1")
    || path.StartsWithSegments("/dashboard")
    || path.StartsWithSegments("/models")
    || path.StartsWithSegments("/api/dashboard")
    || path.StartsWithSegments("/api/models");

static bool IsBlazorFrameworkPath(PathString path) =>
    path.StartsWithSegments("/_framework")
    || path.StartsWithSegments("/_blazor")
    || path.StartsWithSegments("/_content");

app.Use(async (context, next) =>
{
    // Blazor Server 框架端点（静态资源 _framework/blazor.server.js、SignalR /_blazor negotiate）由浏览器
    // 在页面加载后自动发起，无法携带 ?key= 查询参数，必须放行。页面 HTML 自身仍走下面的鉴权。
    if (IsBlazorFrameworkPath(context.Request.Path))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    if (!IsProtectedPath(context.Request.Path))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    // 管理端与代理分离鉴权：管理路径（dashboard/models）优先用 AdminApiKey，
    // 未配置 AdminApiKey 时回退到 ProxyApiKey（保持既有行为，非破坏性）。
    bool isAdminPath = context.Request.Path.StartsWithSegments("/dashboard")
        || context.Request.Path.StartsWithSegments("/api/dashboard")
        || context.Request.Path.StartsWithSegments("/models")
        || context.Request.Path.StartsWithSegments("/api/models");
    string? proxyKey = app.Configuration["OptiRouter:ProxyApiKey"];
    string configuredKey = isAdminPath
        ? (app.Configuration["OptiRouter:AdminApiKey"] ?? "").Length > 0
            ? app.Configuration["OptiRouter:AdminApiKey"]!
            : proxyKey ?? ""
        : proxyKey ?? "";

    string? providedKey = null;
    if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization)
        && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
    {
        providedKey = authorization.Parameter;
    }
    // Dashboard/模型配置浏览器场景：Authorization 头不便携带，支持 ?key= 查询参数（仅 dashboard/models 路径）。
    // 运维侧工具，访问者即 key 持有者；key 入 URL 有日志风险，由调用方/反代负责。
    else if (isAdminPath)
    {
        providedKey = context.Request.Query["key"];
    }

    if (!IsValidApiKey(configuredKey, providedKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // 管理端 key 经 URL/referer 有泄露风险：禁止页面外传 referer，降低泄露面。
    context.Response.Headers["Referrer-Policy"] = "no-referrer";

    await next(context).ConfigureAwait(false);
});

// M2 阶段：分区最大并发数控制，防止单用户请求洪水打满线程池
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/v1"))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    string partitionKey = ResolvePartitionKey(context,
        app.Configuration.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false);

    int maxConcurrency = app.Configuration.GetValue<int?>("OptiRouter:MaxConcurrentRequestsPerPartition") ?? 100;
    var sem = OptiRouter.Concurrency.ConcurrencyRegistry.GetSemaphore(partitionKey, maxConcurrency);

    if (!await sem.WaitAsync(0).ConfigureAwait(false))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = "5";
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Title = "Too many concurrent requests",
            Detail = "Concurrency limit exceeded. Please slow down.",
            Status = StatusCodes.Status429TooManyRequests
        }).ConfigureAwait(false);
        return;
    }

    try
    {
        await next(context).ConfigureAwait(false);
    }
    finally
    {
        sem.Release();
    }
});

app.UseRateLimiter();

// 健康检查端点，无需 API Key，不受限流影响（非 /v1/* 路径）。
app.MapHealthChecks("/health");

// Prometheus 指标导出端点 /metrics，无需 API Key（同 /health，便于抓取），不受限流影响（非 /v1/* 路径）。
// 仅暴露聚合数（请求数/token/成本/延迟）与模型名，不含 API Key 或 PII。
// EnableMetrics=false 时不映射端点（仪表仍登记，但无抓取入口）。
bool enableMetrics = app.Configuration.GetValue<bool?>("OptiRouter:Routing:EnableMetrics") ?? true;
if (enableMetrics)
{
    string metricsPath = app.Configuration.GetValue<string?>("OptiRouter:Routing:MetricsEndpointPath") ?? "/metrics";
    app.UseHttpMetrics(options =>
    {
        // 用自定义 optirouter_request_duration_ms（按模型标签）替代默认 ASP.NET http_request_duration_seconds。
        options.RequestDuration.Enabled = false;
    });
    app.MapMetrics(metricsPath);
}

// 生产环境 HTTPS 检查。
if (builder.Environment.IsProduction())
{
    string? urls = app.Configuration["ASPNETCORE_URLS"];
    bool hasHttps = urls is not null && urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
    if (!hasHttps)
    {
        app.Logger.LogWarning(
            "Production environment without HTTPS. ProxyApiKey will transit in plaintext. " +
            "Configure ASPNETCORE_URLS with https:// or terminate TLS at a reverse proxy.");
    }
}

// t4: 暴露 OpenAI 兼容 Chat Completions 端点。
app.MapChatCompletions();

// OpenAI 兼容模型发现端点（GET /v1/models），受 /v1/* 鉴权与限流保护。
app.MapModelsEndpoint();

// 注册可视化监控 Dashboard 与模型配置页（两页职责分离）
// Blazor Server UI routes: /dashboard and /models are served by _Host.cshtml Razor Pages。
// _Host.cshtml 位于 Pages/Dashboard 和 Pages/Models 子目录，各有 @page，无 Pages/_Host.cshtml 根页，
// 故 MapFallbackToPage 不能指向 /_Host（不存在，会 500）。根路径重定向到 dashboard 作为入口。
app.MapGet("/", context =>
{
    context.Response.Redirect("/dashboard");
    return Task.CompletedTask;
});
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/Dashboard/_Host");

app.MapDashboardEndpoints();
app.MapModelsConfigEndpoints();

app.Run();

static bool IsValidApiKey(string? configuredKey, string? providedKey)
{
    if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrEmpty(providedKey))
        return false;

    byte[] configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
    return CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);
}

// 分区 Key 解析：限流与并发中间件共用。
// 优先级 Session > IP > Auth：
//   - Session：显式会话隔离（最强信号）
//   - IP：网络来源（CF-Connecting-IP > X-Forwarded-For 首段 > RemoteIpAddress），
//         仅当 OptiRouter:TrustProxyHeaders=true 时信任代理头（必须位于可信反代/CF 之后）；
//         否则回退 socket 级 RemoteIpAddress，防止客户端伪造代理头绕过限流/并发限制
//   - Auth：退路（无 session 无 IP 才用），SHA256 前 16 hex 字符，避免明文 key 入分区诊断日志
static string ResolvePartitionKey(HttpContext context, bool trustProxyHeaders)
{
    var headers = context.Request.Headers;

    if (headers.TryGetValue("X-Session-Id", out var sessionIdHeader) && !string.IsNullOrWhiteSpace(sessionIdHeader))
        return $"session:{sessionIdHeader}";

    string? ip = null;
    if (trustProxyHeaders && headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
        ip = cfIp;
    else if (trustProxyHeaders && headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
        ip = xff.ToString().Split(',')[0].Trim();
    else
        ip = context.Connection.RemoteIpAddress?.ToString();

    if (!string.IsNullOrEmpty(ip))
        return $"ip:{ip}";

    if (headers.TryGetValue("Authorization", out var authHeader)
        && authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        string token = authHeader.ToString().Substring("Bearer ".Length).Trim();
        return $"auth:{HashPartitionToken(token)}";
    }

    return "anonymous";
}

static string HashPartitionToken(string token)
{
    // 前 16 hex 字符（64 bit），分区去重足够，且不可逆推原 key。
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
}

public partial class Program { }
