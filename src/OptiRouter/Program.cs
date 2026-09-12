using OptiRouter.Composition;
using OptiRouter.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Components.Services;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Health;
using OptiRouter.Metrics;
using OptiRouter.Routing;
using OptiRouter.Concurrency;
using OptiRouter.Compliance;
using OptiRouter.Compression;
using OptiRouter.Providers;
using OptiRouter.Mcp;
using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;
using Serilog;
using Serilog.Extensions.Logging;

// 初始化 SQLitePCLRaw 原生库（使用 bundle_e_sqlite3）。必须在使用 Microsoft.Data.Sqlite 前调用一次。
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// 日志落地：Serilog 滚动文件（按天分文件 + 单文件 50MB 上限，自动保留最近 14 个，
// 上限约 700MB 后自动淘汰最旧）。替代原自研 TimestampedFileLoggerProvider——
// 其进程生命周期内不轮转，service.log 无限增长；EventLog provider 的关停
// disposed 竞态刷屏问题由 ClearProviders 一并移除。
// SerilogLoggerProvider 以标准 ILoggerProvider 经 DI 接入（UseSerilog/AddSerilog(IServiceCollection)
// 会替换 ILoggerFactory，DI 注册的其他 provider 如测试日志捕获器将收不到消息）。
builder.Logging.ClearProviders();
var serilogLogger = new LoggerConfiguration()
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "service-.log"),
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Level:l4}: {SourceContext}[{EventId}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 50L * 1024 * 1024,
        retainedFileCountLimit: 14,
        flushToDiskInterval: TimeSpan.FromSeconds(2))
    .CreateLogger();
builder.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(serilogLogger, dispose: true));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB limit
});

string configDbPath = builder.Configuration["OptiRouter:ConfigDbPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "optirouter-config.db");
string? configDbConnectionString = builder.Configuration["OptiRouter:ConfigDbConnectionString"];
builder.Services.AddConfigurationStores(builder.Configuration, configDbPath, configDbConnectionString);
builder.Configuration.Sources.Add(new DbAppConfigSource { DbPath = configDbPath, ConnectionString = configDbConnectionString });


// Bind and validate RouterOptions on startup.
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 100_000;
    options.CompactionPercentage = 0.2;
});
builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection("OptiRouter"))
    // models-config.json 是首启种子完成后的权威模型列表。普通 Configure
    // 保留 WebApplicationFactory 后注册 Configure<RouterOptions> 的覆盖能力，
    // 同时在每次 IConfiguration reload 时重新读取文件，避免数组 provider 合并
    // 让 appsettings 中已删除/缩短的模型重新出现。
    .Configure<ModelsConfigService>((options, modelsConfig) =>
    {
        options.Models.Clear();
        foreach (var model in modelsConfig.LoadModels())
            options.Models.Add(model);
    })
    // Name 留空且配置了 Id 的模型在此归一化为 "{供应商}/{Id}"（冲突时追加序号），
    // 后续 Validate 与所有消费方（路由/客户端/显示）看到的都是最终路由名。
    .PostConfigure(options => ModelNameNormalizer.Normalize(options.Models))
    // 可选内置 catalog 注入：配置库为空且 EnableBuiltInProviderCatalog=true（默认关闭——
    // 目录为第三方端点，注入即改变 prompt 数据流向且默认无 ApiKey，须管理员显式开启）时，
    // 注入 BuiltInProviderCatalog 默认 provider 让 auto 路由从零配置可工作。
    // 用户手动配置的模型优先级高于 catalog：只要配置里有一条记录就不注入。
    .PostConfigure(options =>
    {
        if (options.Routing.EnableBuiltInProviderCatalog && options.Models.Count == 0)
        {
            var catalogDefaults = BuiltInProviderCatalog.GetDefaults();
            foreach (var m in catalogDefaults)
                options.Models.Add(m);
        }
    })
    // Budget 的 MariaDB 连接缺省回退全局 OptiRouter:ConfigDbConnectionString——
    // 同一数据库只需配置一处连接（Budget.MariaDbConnectionString 仅作独立库覆盖用）。
    // StoreProvider 默认 Auto：配置了全局连接即 MariaDb，否则回退 SQLite（显式指定可覆盖），
    // 配置里因此不再需要出现 Budget:StoreProvider 开关。
    .PostConfigure(options =>
    {
        string? globalConnection = builder.Configuration["OptiRouter:ConfigDbConnectionString"];
        if (string.Equals(options.Budget.StoreProvider, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            options.Budget.StoreProvider = string.IsNullOrWhiteSpace(globalConnection)
                ? "Sqlite"
                : "MariaDb";
        }

        if (string.Equals(options.Budget.StoreProvider, "MariaDb", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(options.Budget.MariaDbConnectionString))
        {
            options.Budget.MariaDbConnectionString = globalConnection;
        }
    })
    // 应用路由预设（Preset）填充未显式配置的 Routing 项。
    // IServiceProvider 作为 TDep 解析到根容器，再取 IConfiguration 与 ILogger。
    .PostConfigure<IServiceProvider>((options, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILogger<Program>>();
        RoutingPreset.Apply(options.Routing, config, logger);
    })
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<RouterOptions>>(sp =>
    new RouterOptionsValidator(sp.GetRequiredService<ILogger<RouterOptionsValidator>>()));

// 注册模型客户端工厂（传日志，客户端流式解析降级时可诊断）。
builder.Services.AddUpstreamClientServices();
builder.Services.AddStateStoreServices();
builder.Services.AddTracingServices(builder.Configuration);

// t3: 注册成本账本、跨请求模型健康跟踪器（三态断路器）和路由引擎。
// 注册 ClientKeyService（租户 Key 与配额管理；配置 ConfigDbConnectionString 后持久化到 MariaDB，
// 否则默认 client-keys.json 文件）
builder.Services.AddTenantAndLearningServices(builder.Environment.ContentRootPath, configDbConnectionString);

builder.Services.AddSecurityServices(builder.Configuration);

builder.Services.AddProxySupportServices();
builder.Services.AddRoutingDecisionEngine();

builder.Services.AddObservabilityAndBackgroundServices(builder.Configuration);
builder.Services.AddManagementUiServices();

var app = builder.Build();

// 单实例守卫：已有其他进程在运行时干净退出（详见 SingleInstanceGuard）。
// 同进程多 host（集成测试）放行；锁句柄保持到进程结束。
// 默认关闭（集成测试有十余个独立 WebApplicationFactory 宿主，监听随机端口与常驻服务无冲突，
// 却会被跨进程锁误杀致 dotnet test 中止）；生产部署在 appsettings.Production.json 显式开启。
if (app.Configuration.GetValue("OptiRouter:EnableSingleInstanceGuard", false))
{
    var (proceed, singleInstanceLock) = OptiRouter.SingleInstanceGuard.TryAcquire(
        message => app.Logger.LogWarning(message));
    if (!proceed)
    {
        Environment.Exit(0);
    }
    _ = singleInstanceLock; // 锁句柄保持到进程结束（防 GC/析构释放；进程退出自动释放）
}

// 首启迁移：配置库为空时从 appsettings.json 的 Routing/Budget 段与遗留 models-config.json 导入一次。
// 之后 DB 为唯一权威；Reload 让配置提供者读到迁移值（ValidateOnStart 前完成）。
SeedConfigFromLegacySources(
    app.Services.GetRequiredService<AppConfigDbStore>(),
    app.Configuration,
    Path.Combine(app.Environment.ContentRootPath, "models-config.json"),
    string.IsNullOrWhiteSpace(configDbConnectionString)
        ? configDbPath
        : "MariaDB (ConfigDbConnectionString)");
((IConfigurationRoot)app.Configuration).Reload();

// 管理端密钥预热：哈希存配置库 security scope（appsettings 仅首启种子，明文不再进代码库）。
// 库与种子源都缺时会生成随机密钥并在日志打印明文一次。
_ = app.Services.GetRequiredService<OptiRouter.Configuration.AdminKeyStore>();

// 配置热重载时清理 Thompson 采样状态：剔除已删除/改名的模型条目，防 _states 无界泄漏。
// OnChange 在 models-config.json 写入触发 IConfigurationRoot.Reload 后派发。
var tsStoreForReload = app.Services.GetRequiredService<ThompsonStateStore>();
var quotaStoreForReload = app.Services.GetRequiredService<UpstreamQuotaStateStore>();
var banditStoreForReload = app.Services.GetRequiredService<ContextualBanditState>();
var routerOptionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>();
routerOptionsMonitor.OnChange(options =>
{
    tsStoreForReload.Retain(options.Models.Select(m => m.Name));
    quotaStoreForReload.Retain(options.Models.Select(m => m.Name));
    banditStoreForReload.Retain(options.Models.Select(m => m.Name));
});

// 安全基线响应头：nosniff 防 MIME 嗅探；X-Frame-Options/CSP frame-ancestors 防管理台被 iframe
// 嵌入的点击劫持。放在静态文件之前，静态资源响应同样覆盖。
// HSTS 仅生产启用（HTTP 请求浏览器本就忽略该头，本地 HTTP 部署无影响）。
if (builder.Environment.IsProduction())
{
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
    await next(context).ConfigureAwait(false);
});

// Serve the Blazor boot script and the dashboard's CSS/JavaScript before the
// authentication middleware. Framework asset requests cannot carry the admin key.
app.UseStaticFiles();

// 解析登录会话 Cookie（管理端可视化界面鉴权）。
// UseAuthorization 延后到自定义鉴权中间件之后：Minimal Hosting 在管道最前完成路由匹配，
// 未映射的 /v1/* 会落到兜底页 MapFallbackToPage（带 [Authorize]），若 UseAuthorization 先行
// 会触发 Cookie Challenge（302 /login）而非代理协议要求的 401 JSON——API 客户端拿到登录页重定向。
app.UseAuthentication();

app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue("X-Request-Id", out var requestId) || string.IsNullOrEmpty(requestId))
    {
        requestId = Guid.NewGuid().ToString("N");
    }
    context.Response.Headers["X-Request-Id"] = requestId;
    context.Items["RequestId"] = requestId.ToString();

    // 分布式追踪：解析入口 W3C traceparent（缺省则生成新 trace），开 TraceScope 供
    // OutcomeRecorder.RecordAudit 沿 AsyncFlow 读取，贯穿 ProxyOrchestrator/FusionRouter 所有审计点。
    var routingOpts = context.RequestServices.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue.Routing;
    if (routingOpts.EnableDistributedTracing)
    {
        var (traceId, parentSpanId) = DistributedTraceContext.ParseTraceParent(context.Request.Headers["traceparent"]);
        using var scope = TraceScope.Begin(traceId, DistributedTraceContext.GenerateSpanId(), parentSpanId);
        await next(context).ConfigureAwait(false);
    }
    else
    {
        await next(context).ConfigureAwait(false);
    }
});

app.UseMiddleware<RequestAuthenticationMiddleware>();

// M2 阶段：分区最大并发数控制，防止单用户请求洪水打满线程池
// （端点级授权——Blazor Hub RequireAuthorization / Razor 页 [Authorize]——在此之后评估。）
app.UseAuthorization();

// 分区并发闸注册表（DI 单例）：租约化并发控制，见 ConcurrencyRegistry。
var concurrencyRegistry = app.Services.GetRequiredService<ConcurrencyRegistry>();
app.Use(async (context, next) =>
{
    if (!RequestPathPolicy.IsProxyPath(context.Request.Path))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    string partitionKey = RequestIdentity.ResolvePartitionKey(context,
        app.Configuration.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false);

    int maxConcurrency = app.Configuration.GetValue<int?>("OptiRouter:MaxConcurrentRequestsPerPartition") ?? 100;
    // 租约把"引用—获取—可淘汰判定"绑进同一生命周期：等待在注册表内完成，
    // 租约持有期间分区条目不会被空闲扫描淘汰（防同分区新旧两道闸）。
    var lease = concurrencyRegistry.Acquire(partitionKey, maxConcurrency);

    if (lease is null)
    {
        await ProtocolErrorHelper.WriteProxyErrorAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            "Too many concurrent requests",
            "CONCURRENCY_LIMIT_EXCEEDED",
            retryAfterSeconds: 5).ConfigureAwait(false);
        return;
    }

    try
    {
        await next(context).ConfigureAwait(false);
    }
    finally
    {
        lease.Dispose();
    }
});

app.UseRateLimiter();

// OpenAPI 文档（位于 /dashboard/swagger，经上方鉴权中间件保护；openapi.json 随 UI 页同源提供）。
app.UseSwagger(c => c.RouteTemplate = "dashboard/api-docs/{documentName}/openapi.json");
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "dashboard/swagger";
    c.SwaggerEndpoint("api-docs/v1/openapi.json", "OptiRouter v1");
});

// 健康检查端点，无需 API Key，不受限流影响（非 /v1/* 路径）。
app.MapHealthChecks("/health");

// Prometheus 指标导出端点 /metrics，无需 API Key（同 /health，便于抓取），不受限流影响（非 /v1/* 路径）。
// 仅暴露聚合数（请求数/token/成本/延迟）与模型名，不含 API Key 或 PII。
// EnableMetrics=false 时不映射端点（仪表仍登记，但无抓取入口）。
bool enableMetrics = app.Configuration.GetValue<bool?>("OptiRouter:Routing:EnableMetrics") ?? true;
if (enableMetrics)
{
    string metricsPath = app.Configuration.GetValue<string?>("OptiRouter:Routing:MetricsEndpointPath") ?? "/metrics";
    string? metricsApiKey = app.Configuration.GetValue<string?>("OptiRouter:Routing:MetricsApiKey");

    app.UseHttpMetrics(options =>
    {
        // 用自定义 optirouter_request_duration_ms（按模型标签）替代默认 ASP.NET http_request_duration_seconds。
        options.RequestDuration.Enabled = false;
    });

    // 配置了 MetricsApiKey 时，要求 Bearer token 鉴权
    var metricsEndpoint = app.MapMetrics(metricsPath);
    if (!string.IsNullOrWhiteSpace(metricsApiKey))
    {
        metricsEndpoint.AddEndpointFilter(async (context, next) =>
        {
            string? providedKey = RequestIdentity.ExtractApiKey(context.HttpContext);
            if (!AdminKeyVerifier.IsValid(metricsApiKey, providedKey))
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Unauthorized();
            }
            return await next(context);
        });
    }
}

// 生产环境 HTTPS 检查。
if (builder.Environment.IsProduction())
{
    // 读实际生效的绑定地址（urls 配置键或 ASPNETCORE_URLS，前者优先级更高——此前只查环境变量，
    // 生产经 appsettings.Production.json 配置 urls 时告警必然误报）。
    string? urls = app.Configuration["urls"] ?? app.Configuration["ASPNETCORE_URLS"];
    bool hasHttps = urls is not null && urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
    bool loopbackOnly = urls is not null && urls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(u => u.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                  || u.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                  || u.Contains("[::1]", StringComparison.OrdinalIgnoreCase));
    if (!hasHttps && !loopbackOnly)
    {
        app.Logger.LogWarning(
            "Production environment without HTTPS. ProxyApiKey will transit in plaintext. " +
            "Configure ASPNETCORE_URLS with https:// or terminate TLS at a reverse proxy.");
    }
}

// t4: 暴露 OpenAI 兼容 Chat Completions 端点。
app.MapChatCompletions();
// 下游协议对齐：Anthropic Messages 与 Gemini generateContent 原生入口，
// 内部统一翻译为 OpenAI 契约进路由管线（鉴权兼容 x-api-key / x-goog-api-key / ?key=）。
app.MapAnthropicMessages();
app.MapGeminiGenerateContent();

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
var blazorHub = app.MapBlazorHub().RequireAuthorization();
blazorHub.Add(endpointBuilder =>
{
    // MapBlazorHub also registers its public blazor.server.js static-file endpoint.
    // Keep that endpoint anonymous while retaining authorization on the SignalR hub.
    // Match by the /_framework/ route prefix (public URL contract) rather than the
    // framework-internal DisplayName string, which can change across .NET versions.
    if (endpointBuilder is RouteEndpointBuilder routeBuilder
        && routeBuilder.RoutePattern.RawText?.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase) == true)
    {
        endpointBuilder.Metadata.Add(new AllowAnonymousAttribute());
    }
});
app.MapFallbackToPage("/Dashboard/_Host");

app.MapDashboardEndpoints();
app.MapModelsConfigEndpoints();

// ── MCP Server HTTP 端点（JSON-RPC 2.0）────────────────────────────
// 支持 Claude Code / Cline / Cursor 等 MCP 客户端连接 OptiRouter
// POST /mcp           — JSON-RPC 单请求
// POST /mcp/batch     — JSON-RPC batch 请求
// GET  /mcp/tools     — 列出所有可用工具（人类可读）
// /mcp 已纳入 RequestPathPolicy.AdminPathPrefixes：外部 MCP 客户端携带 Bearer <AdminApiKey>，
// 浏览器访问走登录 Cookie；未认证请求由自定义鉴权中间件 401 拦截（不 302 登录页）。
var mcpOptions = app.Services.GetRequiredService<OptiRouter.Mcp.McpServerOptions>();
var mcpServer = app.Services.GetRequiredService<OptiRouter.Mcp.McpServerHost>();

if (mcpOptions.Enabled)
{
    app.MapPost(mcpOptions.Path, async (HttpContext ctx) =>
    {
        using var reader = new StreamReader(ctx.Request.Body);
        string body = await reader.ReadToEndAsync();
        var response = await mcpServer.HandleRequestAsync(body, ctx.RequestAborted);

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = response.StatusCode;
        await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
    });

    app.MapPost($"{mcpOptions.Path}/batch", async (HttpContext ctx) =>
    {
        using var reader = new StreamReader(ctx.Request.Body);
        string body = await reader.ReadToEndAsync();
        var response = await mcpServer.HandleRequestAsync(body, ctx.RequestAborted);

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = response.StatusCode;
        await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
    });

    app.MapGet($"{mcpOptions.Path}/tools", async (HttpContext ctx) =>
    {
        var response = await mcpServer.HandleRequestAsync(
            """{"jsonrpc":"2.0","method":"tools/list","params":{},"id":1}""",
            ctx.RequestAborted);

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = response.StatusCode;
        await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
    });
}

app.Run();

public partial class Program
{
    /// <summary>
    /// 首启迁移：配置库无数据时，把 appsettings.json 的 Routing/Budget 段与遗留 models-config.json
    /// 的模型列表导入数据库。之后 DB 为唯一权威，不再读取文件配置。
    /// </summary>
    internal static void SeedConfigFromLegacySources(
        AppConfigDbStore store,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        string legacyModelsPath,
        string configDbDescription)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configuration);

        if (store.HasData())
            return;

        bool seeded = false;
        var routing = SectionToJson(configuration.GetSection("OptiRouter:Routing")) as JsonObject;
        if (routing is { Count: > 0 })
        {
            store.SaveDocument(AppConfigDbStore.RoutingScope, routing.ToJsonString());
            seeded = true;
        }

        var budget = SectionToJson(configuration.GetSection("OptiRouter:Budget")) as JsonObject;
        if (budget is { Count: > 0 })
        {
            store.SaveDocument(AppConfigDbStore.BudgetScope, budget.ToJsonString());
            seeded = true;
        }

        if (!string.IsNullOrEmpty(legacyModelsPath) && File.Exists(legacyModelsPath))
        {
            try
            {
                var legacy = System.Text.Json.JsonSerializer.Deserialize<List<ModelEndpointOptions>>(
                    File.ReadAllText(legacyModelsPath),
                    AppConfigDbStore.ModelsFileJsonOptions);
                if (legacy is { Count: > 0 })
                {
                    store.SaveModels(legacy);
                    seeded = true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SeedConfigFromLegacySources] failed to import '{legacyModelsPath}': {ex.Message}");
            }
        }

        if (seeded)
            Console.WriteLine($"[SeedConfigFromLegacySources] migrated appsettings Routing/Budget + models-config.json into config database ({configDbDescription})");
    }

    /// <summary>IConfiguration 节 → JsonNode（数字键子节收敛为数组，保证 SemanticRoutes 等数组形态正确）。</summary>
    private static JsonNode? SectionToJson(Microsoft.Extensions.Configuration.IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return ParseScalar(section.Value);

        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            var arr = new JsonArray();
            foreach (var c in children.OrderBy(c => int.Parse(c.Key)))
                arr.Add(SectionToJson(c));
            return arr;
        }

        var obj = new JsonObject();
        foreach (var c in children)
            obj[c.Key] = SectionToJson(c);
        return obj;
    }

    private static JsonNode? ParseScalar(string? value)
    {
        if (value is null)
            return null;
        if (bool.TryParse(value, out bool b))
            return JsonValue.Create(b);
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal d))
            return JsonValue.Create(d);
        return JsonValue.Create(value);
    }
}
