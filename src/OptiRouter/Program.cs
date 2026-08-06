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

// Bind and validate RouterOptions on startup.
builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection("OptiRouter"))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<RouterOptions>, RouterOptionsValidator>();

// 注册模型客户端工厂。
builder.Services.AddSingleton<ModelClientFactory>();

// 注册模型客户端提供者（生产实现，按模型名缓存 IModelClient）。
builder.Services.AddSingleton<IModelClientProvider, ModelClientProvider>();

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

// t3: 注册成本账本、跨请求模型健康跟踪器（熔断）和路由引擎。
builder.Services.AddSingleton<CostLedger>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    var store = sp.GetRequiredService<ICostLedgerStore>();
    return new CostLedger(store, options.Budget.SessionEvictionHours);
});
builder.Services.AddSingleton<ModelHealthTracker>();
builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    // 策略链在请求处理时读取 IOptionsMonitor.CurrentValue（ProxyOrchestrator 注入）。
    // 注意：Models 端点配置（BaseUrl/ApiKey/Timeout）按模型名缓存于 ModelClientProvider，
    // 变更需重启进程生效；Routing 开关经 IOptionsMonitor reload 后生效，与此缓存语义解耦。
    var policies = new List<IRouterPolicy>
    {
        new RuleClassifierPolicy(),
        new LongInputPolicy(),
        new BudgetGuardPolicy(ledger),
        new FailoverPolicy(healthTracker)
    };
    return new RouterEngine(ledger, policies);
});

// t4: 注册降级重试编排器。
builder.Services.AddSingleton<ProxyOrchestrator>();

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

        string sourceIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(sourceIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = requestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/v1"))
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

    if (!IsValidApiKey(configuredKey, providedKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context).ConfigureAwait(false);
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

app.Run();

static bool IsValidApiKey(string? configuredKey, string? providedKey)
{
    if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrEmpty(providedKey))
        return false;

    byte[] configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
    return CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);
}
