using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
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
using Prometheus;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;

// 初始化 SQLitePCLRaw 原生库（使用 bundle_e_sqlite3）。必须在使用 Microsoft.Data.Sqlite 前调用一次。
SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB limit
});

// 注册 models-config.json 为配置源（后于 appsettings.json，覆盖 OptiRouter:Models 段）。
// Dashboard 写入该文件后 ModelsConfigService 触发 IConfigurationRoot.Reload()，IOptionsMonitor 派发到 router。
// 首启种子：models-config.json 不存在时，把 appsettings.json 的 Models 段写入一次。
// 合法空数组是权威配置，不得在重启时被 appsettings.json 的模型重新填充。
string modelsConfigPath = Path.Combine(builder.Environment.ContentRootPath, "models-config.json");
if (!File.Exists(modelsConfigPath))
{
    try
    {
        Program.SeedModelsConfig(builder.Configuration, modelsConfigPath);
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

// 成本账本存储：支持 "Postgres" | "Redis" | "Sqlite" | "InMemory"。
// 对于 K8s 多节点部署架构，配置 "Postgres" 或 "Redis" 即可实现跨节点全局成本计费与断路器共享。
builder.Services.AddSingleton<ICostLedgerStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        return new PostgresCostLedgerStore(options.Budget.PostgresConnectionString);
    }
    if (string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
    {
        return new RedisCostLedgerStore(options.Budget.RedisConnectionString, options.Budget.RedisKeyPrefix);
    }
    if (!options.Budget.UsePersistentStore || string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
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

// 请求审计存储：支持 "Postgres" | "Sqlite" | "InMemory"。
builder.Services.AddSingleton<IRequestAuditStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        return new PostgresRequestAuditStore(options.Budget.PostgresConnectionString);
    }
    if (!options.Budget.UsePersistentStore || string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
    {
        return new InMemoryRequestAuditStore();
    }

    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return new SqliteRequestAuditStore(storePath, sp.GetRequiredService<ILogger<SqliteRequestAuditStore>>());
});

// 配置 OpenTelemetry OTLP Exporter 链路追踪导出（无缝对接 Jaeger, Tempo 或 Datadog）
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        var routingOptions = builder.Configuration.GetSection("OptiRouter:Routing").Get<RoutingOptions>() ?? new RoutingOptions();

        tracerProviderBuilder
            .SetResourceBuilder(OpenTelemetry.Resources.ResourceBuilder.CreateDefault().AddService(routingOptions.OtlpServiceName ?? "OptiRouter"))
            .AddSource("OptiRouter.Tracing");

        if (routingOptions.EnableOtlpTracing && !string.IsNullOrWhiteSpace(routingOptions.OtlpEndpoint))
        {
            tracerProviderBuilder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(routingOptions.OtlpEndpoint);
                otlpOptions.Protocol = string.Equals(routingOptions.OtlpProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                    ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
                    : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
        }
    });

// t3: 注册成本账本、跨请求模型健康跟踪器（三态断路器）和路由引擎。
// 注册 ClientKeyService
builder.Services.AddSingleton<ClientKeyService>(sp => new ClientKeyService(Path.Combine(builder.Environment.ContentRootPath, "data", "client-keys.json"), sp.GetRequiredService<ILogger<ClientKeyService>>()));

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

builder.Services.AddSingleton<ISemanticVectorEngine>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    var logger = sp.GetRequiredService<ILogger<OnnxEmbeddingVectorEngine>>();

    if (options.Routing.EnableOnnxEmbedding && !string.IsNullOrWhiteSpace(options.Routing.OnnxModelPath))
    {
        var onnxEngine = new OnnxEmbeddingVectorEngine(
            options.Routing.OnnxModelPath,
            options.Routing.OnnxExecutionProvider,
            fallbackEngine: new DenseEmbeddingVectorEngine(),
            logger: logger);

        return new HybridSemanticVectorEngine(
            sparseEngine: new TfIdfSemanticVectorEngine(),
            denseEngine: onnxEngine,
            highConfidenceThreshold: options.Routing.HybridHighConfidenceThreshold);
    }

    return new HybridSemanticVectorEngine(
        sparseEngine: new TfIdfSemanticVectorEngine(),
        denseEngine: new DenseEmbeddingVectorEngine(),
        highConfidenceThreshold: options.Routing.HybridHighConfidenceThreshold);
});

// Thompson 采样 + Contextual Bandit 状态持久化（共享同一 SQLite 文件）。
builder.Services.AddSingleton<IThompsonStateStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    if (!options.Budget.UsePersistentStore)
        return NullLearningStateStore.Instance;
    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
    return new SqliteLearningStateStore(storePath);
});
// Bandit 复用 Thompson 侧的同一持久化实例（两者写同一 DB 的不同表）。
builder.Services.AddSingleton<IBanditStateStore>(sp =>
{
    var tsStore = sp.GetRequiredService<IThompsonStateStore>();
    return tsStore is SqliteLearningStateStore sqlite ? sqlite : NullLearningStateStore.Instance;
});

builder.Services.AddSingleton<ThompsonStateStore>(sp =>
{
    var persistence = sp.GetRequiredService<IThompsonStateStore>();
    var logger = sp.GetRequiredService<ILogger<ThompsonStateStore>>();
    return new ThompsonStateStore(persistence, logger);
});
builder.Services.AddSingleton<ContextualBanditState>(sp =>
{
    var persistence = sp.GetRequiredService<IBanditStateStore>();
    var logger = sp.GetRequiredService<ILogger<ContextualBanditState>>();
    return new ContextualBanditState(persistence: persistence, logger: logger);
});
builder.Services.AddSingleton<UpstreamQuotaStateStore>();
builder.Services.AddSingleton<PromptCacheAffinityStore>();
builder.Services.AddSingleton<FusionPanelSelector>();
builder.Services.AddHttpContextAccessor();

// 管理端登录会话（Cookie）：可视化界面仅管理员登录后可用。
// /v1/* 代理端点不受此影响（仍走 ProxyApiKey + 租户 ClientKeyService）。
// 默认强制 HTTPS（Cookie 仅经 HTTPS 下发，防中间人窃取）；纯内网 HTTP 部署可在 appsettings 设 OptiRouter:AdminCookieRequireHttps=false。
bool adminCookieRequireHttps = builder.Configuration.GetValue<bool?>("OptiRouter:AdminCookieRequireHttps") ?? true;
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "OptiRouter.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = adminCookieRequireHttps
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
    });

// 管理端登录失败限流（单实例内存，按 IP 计数）。
builder.Services.AddSingleton<LoginRateLimiter>();

builder.Services.AddSingleton<IResponseCache>(sp => new MemoryResponseCache(
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<IOptions<RouterOptions>>().Value.Routing.ResponseCacheMaxEntries,
    useSize: true)); // AddMemoryCache 设了 SizeLimit，entry 须申报 Size
// 同一实例的具体类型注册：MaxEntries 在构造时绑定（重启生效），dashboard 状态端点借它读命中/写入统计。
builder.Services.AddSingleton(sp => (MemoryResponseCache)sp.GetRequiredService<IResponseCache>());

builder.Services.AddSingleton<ISemanticResponseCache>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new SemanticResponseCache(options.Routing.SemanticCacheMaxEntries, sp.GetService<ISemanticVectorEngine>());
});

builder.Services.AddSingleton<IAdaptiveConcurrencyLimiter>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new AdaptiveConcurrencyLimiter(options.Routing.AdaptiveMinLimit, options.Routing.AdaptiveMaxLimit);
});

builder.Services.AddSingleton<IStreamingComplianceFilter>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new StreamingSlidingWindowFilter(options.Routing);
});

builder.Services.AddSingleton<KalmanLatencyTracker>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new KalmanLatencyTracker(
        targetLatencyMs: options.Routing.KalmanTargetLatencyMs,
        penaltyGamma: options.Routing.KalmanPenaltyGamma);
});

builder.Services.AddSingleton<KvCachePrefixTrie>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new KvCachePrefixTrie(TimeSpan.FromMinutes(options.Routing.KvCacheTtlMinutes));
});

builder.Services.AddSingleton<ReasoningEffortController>();
builder.Services.AddSingleton<MultiAgentDagRouter>();
builder.Services.AddSingleton<CrossProviderSpeculativeEngine>();
builder.Services.AddSingleton<ByzantineConsensusEngine>(sp =>
    new ByzantineConsensusEngine(sp.GetService<ISemanticVectorEngine>()));
builder.Services.AddSingleton<PredictiveResilienceEngine>();

builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var tokenEstimator = sp.GetRequiredService<ITokenEstimator>();
    var vectorEngine = sp.GetRequiredService<ISemanticVectorEngine>();
    var tsStore = sp.GetRequiredService<ThompsonStateStore>();
    var kalmanTracker = sp.GetRequiredService<KalmanLatencyTracker>();
    var kvCacheTrie = sp.GetRequiredService<KvCachePrefixTrie>();
    var resilienceEngine = sp.GetRequiredService<PredictiveResilienceEngine>();
    // 策略链在请求处理时读取 IOptionsMonitor.CurrentValue（ProxyOrchestrator 注入），
    // Tier/价格等字段 reload 后立即生效；Models 端点连接配置（BaseUrl/ApiKey/Timeout）
    // 缓存于 ModelClientProvider，经 OnChange 热更新重建（见其注册处）。
    var policies = new List<IRouterPolicy>
    {
        // 显式模型固定必须最先执行：把资格池缩到指定模型后，
        // 后续 Filter/Classify/Order 策略只能在单元素池内工作，不会换模型。
        new ExplicitModelPolicy(),
        new DataSovereigntyPolicy(),
        new CapabilityFilterPolicy(),
        new RuleClassifierPolicy(),
        new SessionAffinityPolicy(sp.GetRequiredService<IMemoryCache>()),
        new SemanticRouterPolicy(vectorEngine),
        new LongInputPolicy(),
        new LatencyAwarePolicy(sp.GetRequiredService<ILatencyStatsProvider>(), tsStore, null,
            sp.GetRequiredService<ContextualBanditState>()),
        new PromptCacheAffinityPolicy(sp.GetRequiredService<PromptCacheAffinityStore>()),
        new KvCacheLocalityPolicy(kvCacheTrie),
        new PredictiveResiliencePolicy(resilienceEngine),
        new ParetoFrontierPolicy(),
        new BudgetGuardPolicy(ledger),
        new QuotaAwarePolicy(sp.GetRequiredService<UpstreamQuotaStateStore>()),
        new FailoverPolicy(healthTracker),
        new LoadBalancePolicy(kalmanTracker)
    };
    return new RouterEngine(ledger, policies, tokenEstimator);
});

// t4: 注册降级重试编排器。
// 构造依赖（RouterEngine/IOptionsMonitor/ModelHealthTracker/OutcomeRecorder/ILogger）由 DI 自动注入。
builder.Services.AddSingleton<OutcomeRecorder>(sp => new OutcomeRecorder(
    sp.GetRequiredService<IRequestAuditStore>(),
    sp.GetRequiredService<OptiRouter.Metrics.RouterMetrics>(),
    sp.GetRequiredService<CostLedger>(),
    sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<ThompsonStateStore>(),
    sp.GetRequiredService<PromptCacheAffinityStore>(),
    sp.GetRequiredService<UpstreamQuotaStateStore>(),
    sp.GetRequiredService<ILogger<OutcomeRecorder>>(),
    banditStore: sp.GetRequiredService<ContextualBanditState>(),
    clientKeyService: sp.GetRequiredService<ClientKeyService>(),
    httpContextAccessor: sp.GetRequiredService<IHttpContextAccessor>(),
    kalmanTracker: sp.GetRequiredService<KalmanLatencyTracker>(),
    kvCacheTrie: sp.GetRequiredService<KvCachePrefixTrie>(),
    resilienceEngine: sp.GetRequiredService<PredictiveResilienceEngine>()));
builder.Services.AddSingleton<CascadeUpgradeHandler>();
builder.Services.AddSingleton<FusionRouter>();
builder.Services.AddSingleton<RaceOrchestrator>();
// regenerate 负反馈跟踪器：进程内状态，供 ProxyOrchestrator 在同键请求重发时注入惩罚 reward。
builder.Services.AddSingleton<RegenerateFeedbackTracker>();
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
// ApiService 必须 Scoped（circuit 内共享）：Blazor Server 页面间 NavLink 导航 URL 不含 ?key=，
// Transient 会在每次组件解析时新建实例并从当前 URL 重新提取 key → 导航后全部 401。
builder.Services.AddHttpClient(nameof(ApiService), client => client.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddScoped<ApiService>(sp =>
{
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ApiService));
    var nav = sp.GetRequiredService<NavigationManager>();
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    return new ApiService(client, nav, httpContextAccessor);
});

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

if (string.IsNullOrWhiteSpace(app.Configuration["OptiRouter:AdminApiKey"]))
{
    app.Logger.LogWarning("OptiRouter:AdminApiKey 未配置：管理控制台登录与管理 API 鉴权将全部失败。请配置独立的 AdminApiKey（不应与 ProxyApiKey 复用）。");
}

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

// Serve the Blazor boot script and the dashboard's CSS/JavaScript before the
// authentication middleware. Framework asset requests cannot carry the admin key.
app.UseStaticFiles();

// 解析登录会话 Cookie（管理端可视化界面鉴权）。
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

static bool IsProtectedPath(PathString path) =>
    path.StartsWithSegments("/v1")
    || path.StartsWithSegments("/dashboard")
    || path.StartsWithSegments("/overview")
    || path.StartsWithSegments("/requests")
    || path.StartsWithSegments("/models")
    || path.StartsWithSegments("/router")
    || path.StartsWithSegments("/keys")
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

    // 管理端与代理分离鉴权：
    //   - 管理路径（dashboard/models 页面与 /api/dashboard、/api/models）：优先放行已登录会话（Cookie），
    //     兼容 Authorization: Bearer <AdminApiKey>（脚本/测试客户端）。未认证的页面请求 302 到 /login，API 请求 401。
    //   - /v1/* 代理路径：ProxyApiKey 或租户 ClientKeyService（保持不变）。
    bool isAdminPath = context.Request.Path.StartsWithSegments("/dashboard")
        || context.Request.Path.StartsWithSegments("/overview")
        || context.Request.Path.StartsWithSegments("/requests")
        || context.Request.Path.StartsWithSegments("/models")
        || context.Request.Path.StartsWithSegments("/router")
        || context.Request.Path.StartsWithSegments("/keys")
        || context.Request.Path.StartsWithSegments("/api/dashboard")
        || context.Request.Path.StartsWithSegments("/api/models");
    bool isV1Path = context.Request.Path.StartsWithSegments("/v1");
    string? proxyKey = app.Configuration["OptiRouter:ProxyApiKey"];
    // 管理端密钥仅 AdminApiKey（不再回退 ProxyApiKey）：ProxyApiKey 发给 API 客户端，
    // 允许它登录管理台构成权限越界。未配 AdminApiKey 时管理台鉴权总失败（仅剩已登录会话，会话过期即不可用）。
    string adminKey = app.Configuration["OptiRouter:AdminApiKey"] ?? "";

    if (isV1Path && AdminKeyVerifier.IsValid(proxyKey, ExtractBearerToken(context)))
    {
        // The global proxy key remains compatible and is deliberately not sent through
        // ClientKeyService, so it never consumes a tenant's QPS window or daily budget.
    }
    else if (isV1Path && !isAdminPath)
    {
        var authorizationResult = context.RequestServices
            .GetRequiredService<ClientKeyService>()
            .AuthorizeRequest(ExtractBearerToken(context));

        switch (authorizationResult.Status)
        {
            case ClientKeyAuthorizationStatus.Authorized:
                // Keep the complete immutable identity for OutcomeRecorder and other request
                // scoped consumers without changing their public method signatures.
                context.Items[typeof(ClientKeyAuthorizationResult)] = authorizationResult;
                break;

            case ClientKeyAuthorizationStatus.RateLimited:
                await WriteClientKeyProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Client key rate limit exceeded",
                    authorizationResult.RetryAfterSeconds).ConfigureAwait(false);
                return;

            case ClientKeyAuthorizationStatus.BudgetExhausted:
                await WriteClientKeyProblemAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Client key daily budget exhausted",
                    authorizationResult.RetryAfterSeconds).ConfigureAwait(false);
                return;

            case ClientKeyAuthorizationStatus.Invalid:
            case ClientKeyAuthorizationStatus.Disabled:
            default:
                await WriteClientKeyProblemAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized").ConfigureAwait(false);
                return;
        }
    }
    else if (isAdminPath)
    {
        bool sessionAuthenticated = context.User.Identity?.IsAuthenticated == true;
        bool bearerAuthenticated = AdminKeyVerifier.IsValid(adminKey, ExtractBearerToken(context));

        if (!sessionAuthenticated && !bearerAuthenticated)
        {
            // 页面（HTML）场景：浏览器重定向到登录页；API 场景：直接 401。
            bool isPageRequest = !context.Request.Path.StartsWithSegments("/api");
            if (isPageRequest)
            {
                context.Response.Redirect("/login");
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    else if (!AdminKeyVerifier.IsValid(proxyKey, ExtractBearerToken(context)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // 管理端 key 经 URL/referer 有泄露风险：禁止页面外传 referer，降低泄露面。
    context.Response.Headers["Referrer-Policy"] = "no-referrer";

    await next(context).ConfigureAwait(false);
});

static string? ExtractBearerToken(HttpContext context)
{
    if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization)
        && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
    {
        return authorization.Parameter;
    }
    return null;
}

static async Task WriteClientKeyProblemAsync(
    HttpContext context,
    int statusCode,
    string title,
    int retryAfterSeconds = 0)
{
    context.Response.StatusCode = statusCode;
    if (retryAfterSeconds > 0)
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        Status = statusCode,
        Title = title
    }).ConfigureAwait(false);
}

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

public partial class Program
{
    internal static void SeedModelsConfig(
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(filePath);

        var seeded = configuration.GetSection("OptiRouter:Models")
            .Get<List<OptiRouter.Configuration.ModelEndpointOptions>>();
        string? parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        System.Text.Json.JsonSerializer.Serialize(stream,
            seeded ?? new List<OptiRouter.Configuration.ModelEndpointOptions>(),
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            });
    }
}
