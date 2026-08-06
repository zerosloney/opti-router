using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

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

// t3: 注册成本账本、跨请求模型健康跟踪器（熔断）和路由引擎。
builder.Services.AddSingleton<CostLedger>();
builder.Services.AddSingleton<ModelHealthTracker>();
builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    // 策略链全部注册，每个策略 Apply 内依据当前 RoutingOptions 开关决定是否生效，
    // 以支持配置热更新（开关切换无需重启）。
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

app.MapGet("/health", () => "ok");

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
