using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Health;
using OptiRouter.Routing;

// 初始化 SQLitePCLRaw 原生库（使用 bundle_e_sqlite3）。必须在使用 Microsoft.Data.Sqlite 前调用一次。
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB limit
});

// Bind and validate RouterOptions on startup.
builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection("OptiRouter"))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<RouterOptions>, RouterOptionsValidator>();

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

builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var tokenEstimator = sp.GetRequiredService<ITokenEstimator>();
    // 策略链在请求处理时读取 IOptionsMonitor.CurrentValue（ProxyOrchestrator 注入），
    // Tier/价格等字段 reload 后立即生效；Models 端点连接配置（BaseUrl/ApiKey/Timeout）
    // 缓存于 ModelClientProvider，经 OnChange 热更新重建（见其注册处）。
    var policies = new List<IRouterPolicy>
    {
        new RuleClassifierPolicy(),
        new SemanticRouterPolicy(),
        new LongInputPolicy(),
        new BudgetGuardPolicy(ledger),
        new FailoverPolicy(healthTracker)
    };
    return new RouterEngine(ledger, policies, tokenEstimator);
});

// t4: 注册降级重试编排器。
builder.Services.AddSingleton<ProxyOrchestrator>();

// 后台定时主动探活：启动预热一轮，随后按 HealthProbeIntervalSeconds 周期对所有启用模型探测，
// 结果上报 ModelHealthTracker（成功累计半开/闭合，失败计熔断）。EnableHealthProbe=false 可关闭。
builder.Services.AddHostedService<ModelHealthProbeService>();

// 健康检查：验证内部依赖（成本账本 store 连接正常）。
builder.Services.AddHealthChecks()
    .AddCheck<CostLedgerHealthCheck>("cost-ledger", failureStatus: HealthStatus.Unhealthy);

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

        string partitionKey = ResolvePartitionKey(context);

        // 每请求从已合并的 IConfiguration 读阈值（含 WebApplicationFactory 经 ConfigureAppConfiguration 注入的值）。
        // 注意：FixedWindowRateLimiter 的 PermitLimit 在分区首次创建时定型，运行时改配置仅对新建分区生效，
        // 既有分区沿用创建时的值——变更全局生效需重启进程。这是 ASP.NET 限流器的固有约束，非可热更。
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
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
    path.StartsWithSegments("/v1") || path.StartsWithSegments("/dashboard") || path.StartsWithSegments("/api/dashboard");

app.Use(async (context, next) =>
{
    if (!IsProtectedPath(context.Request.Path))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    string? configuredKey = app.Configuration["OptiRouter:ProxyApiKey"];
    string? providedKey = null;
    if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization)
        && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
    {
        providedKey = authorization.Parameter;
    }
    // Dashboard 浏览器场景：Authorization 头不便携带，支持 ?key= 查询参数（仅 dashboard 路径）。
    // 运维侧工具，访问者即 key 持有者；key 入 URL 有日志风险，由调用方/反代负责。
    else if (context.Request.Path.StartsWithSegments("/dashboard") || context.Request.Path.StartsWithSegments("/api/dashboard"))
    {
        providedKey = context.Request.Query["key"];
    }

    if (!IsValidApiKey(configuredKey, providedKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

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

    string partitionKey = ResolvePartitionKey(context);

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

// 注册可视化配置和健康监控 Dashboard
app.MapDashboardEndpoints();

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
//   - IP：网络来源（CF-Connecting-IP > X-Forwarded-For 首段 > RemoteIpAddress）
//         Auth 头不再压制 IP——多用户共享同一 API key 时仍按来源 IP 隔离，避免单 key 拖垮全租户
//   - Auth：退路（无 session 无 IP 才用），SHA256 前 16 hex 字符，避免明文 key 入分区诊断日志
static string ResolvePartitionKey(HttpContext context)
{
    var headers = context.Request.Headers;

    if (headers.TryGetValue("X-Session-Id", out var sessionIdHeader) && !string.IsNullOrWhiteSpace(sessionIdHeader))
        return $"session:{sessionIdHeader}";

    string? ip = null;
    if (headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
        ip = cfIp;
    else if (headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
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
