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

// 配置存储：Routing/Budget/模型配置全部落配置库（StoreProvider 默认 Auto：有 ConfigDbConnectionString 即 MariaDB，否则 SQLite）。
// SQLite 后端可由 ConfigDbPath 指定文件路径（默认 data/optirouter-config.db）。
// 页面写入经 AppConfigDbStore → IConfigurationRoot.Reload() 热生效。
// 首启迁移：库为空时从 appsettings.json 的 Routing/Budget 段与遗留 models-config.json 导入一次，
// 之后 DB 为唯一权威，appsettings.json 仅保留部署级设置（密钥/端口/限流/DB 连接）。
string configDbPath = builder.Configuration["OptiRouter:ConfigDbPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "optirouter-config.db");
string? configDbConnectionString = builder.Configuration["OptiRouter:ConfigDbConnectionString"];
builder.Services.AddSingleton(sp => new AppConfigDbStore(configDbPath, configDbConnectionString));
// 管理端密钥：哈希存配置库 security scope（构造时种子；appsettings 仅首启种子源）。
builder.Services.AddSingleton(sp => new OptiRouter.Configuration.AdminKeyStore(
    sp.GetRequiredService<AppConfigDbStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetService<ILogger<OptiRouter.Configuration.AdminKeyStore>>()));
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
builder.Services.AddSingleton<ModelClientFactory>(sp =>
    new ModelClientFactory(sp.GetService<ILogger<ModelClientFactory>>()));

// 注册模型客户端提供者（生产实现，按模型名缓存 IModelClient）。
// 热更新：内部订阅 IOptionsMonitor.OnChange，BaseUrl/ApiKey/TimeoutSeconds 变化时重建对应客户端，
// 旧客户端保留一段宽限期后释放，不打断在途请求。
builder.Services.AddSingleton<IModelClientProvider>(sp => new ModelClientProvider(
    sp.GetRequiredService<ModelClientFactory>(),
    sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
    healthTracker: sp.GetRequiredService<ModelHealthTracker>()));

// 成本账本存储：支持 "MariaDb" | "Postgres" | "Redis" | "Sqlite" | "InMemory"。
// 对于 K8s 多节点部署架构，配置 "MariaDb"、"Postgres" 或 "Redis" 即可实现跨节点全局成本计费与断路器共享。
builder.Services.AddSingleton<ICostLedgerStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "MariaDb", StringComparison.OrdinalIgnoreCase))
    {
        return new MariaDbCostLedgerStore(options.Budget.MariaDbConnectionString,
            logger: sp.GetService<ILogger<MariaDbCostLedgerStore>>(),
            alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>());
    }
    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        return new PostgresCostLedgerStore(options.Budget.PostgresConnectionString,
            logger: sp.GetService<ILogger<PostgresCostLedgerStore>>());
    }
    if (string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
    {
        return new RedisCostLedgerStore(options.Budget.RedisConnectionString, options.Budget.RedisKeyPrefix,
            logger: sp.GetService<ILogger<RedisCostLedgerStore>>(),
            alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>());
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

// 请求审计存储：支持 "MariaDb" | "Postgres" | "Sqlite" | "InMemory"。
builder.Services.AddSingleton<IRequestAuditStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "MariaDb", StringComparison.OrdinalIgnoreCase))
    {
        return new MariaDbRequestAuditStore(options.Budget.MariaDbConnectionString!,
            sp.GetService<ILogger<MariaDbRequestAuditStore>>());
    }
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

// 配置 OpenTelemetry OTLP Exporter 链路追踪导出（无缝对接 Jaeger, Tempo 或 Datadog）。
// OTLP 为部署级设置，保留在 appsettings.json（OptiRouter:Otlp* 顶层键），不随 Routing 落库。
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        string? otlpServiceName = builder.Configuration["OptiRouter:OtlpServiceName"];
        bool enableOtlpTracing = builder.Configuration.GetValue<bool?>("OptiRouter:EnableOtlpTracing") ?? false;
        string? otlpEndpoint = builder.Configuration["OptiRouter:OtlpEndpoint"];
        string? otlpProtocol = builder.Configuration["OptiRouter:OtlpProtocol"];

        tracerProviderBuilder
            .SetResourceBuilder(OpenTelemetry.Resources.ResourceBuilder.CreateDefault().AddService(
                string.IsNullOrEmpty(otlpServiceName) ? "OptiRouter" : otlpServiceName))
            .AddSource("OptiRouter.Tracing");

        if (enableOtlpTracing && !string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracerProviderBuilder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(otlpEndpoint);
                otlpOptions.Protocol = string.Equals(otlpProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                    ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
                    : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
        }
    });

// t3: 注册成本账本、跨请求模型健康跟踪器（三态断路器）和路由引擎。
// 注册 ClientKeyService（租户 Key 与配额管理；配置 ConfigDbConnectionString 后持久化到 MariaDB，
// 否则默认 client-keys.json 文件）
builder.Services.AddSingleton<ClientKeyService>(sp => new ClientKeyService(
    Path.Combine(builder.Environment.ContentRootPath, "data", "client-keys.json"),
    sp.GetRequiredService<ILogger<ClientKeyService>>(),
    mariaDbConnectionString: configDbConnectionString,
    alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>()));

builder.Services.AddSingleton<CostLedger>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    var store = sp.GetRequiredService<ICostLedgerStore>();
    return new CostLedger(store, options.Budget.SessionEvictionHours);
});
builder.Services.AddSingleton<ModelHealthTracker>(sp =>
{
    var store = sp.GetRequiredService<ICostLedgerStore>();
    var tracker = new ModelHealthTracker(store);

    // 真实流量成败同步到连通状态留痕：探活（EnableHealthProbe）关闭时，模型配置页
    // "连通状态"列不再只靠手动测试，而由真实请求的成败持续驱动。回调在熔断锁内触发，
    // 须快速非阻塞；异常自行兜底（记日志不上抛），不拖垮路由热路径。
    var probeResults = sp.GetRequiredService<OptiRouter.Health.ProbeResultStore>();
    var syncLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("OptiRouter.Routing.ModelHealthTracker");
    tracker.OutcomeObserved += (modelName, success) =>
    {
        try
        {
            // latencyMs 记 0：tracker 收敛点无耗时上下文（串行/Fusion/Race 各自持有计时器），
            // 消息文案与探活区分；如需精确延迟可升级为 Record* 带 latency 参数。
            probeResults.Record(modelName, new OptiRouter.Health.ProbeStatus(
                success, 0, DateTime.UtcNow,
                success ? "真实请求成功（自动同步）" : "真实请求失败（自动同步）", null));
        }
        catch (Exception ex)
        {
            syncLogger.LogWarning(ex, "连通状态留痕同步失败: {Model}", modelName);
        }
    };

    return tracker;
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

// 告警历史环形缓冲：告警出现/恢复事件进程内留痕，供 Dashboard 历史查询。
builder.Services.AddSingleton<OptiRouter.Health.AlertHistory>();

// 最近探活结果留痕：手动 + 后台探活统一写入，模型配置页"连通状态"列刷新后预填。
builder.Services.AddSingleton<OptiRouter.Health.ProbeResultStore>();

// 审计分析：时间窗全量聚合报告（总览/分模型/分档/级联/Fusion/路由原因/日趋势），供策略调优闭环。
builder.Services.AddSingleton<AuditAnalysisService>();

// Token 估算器：Tiktoken 模式用 SharpToken 真实 BPE 计数（内置词表、离线可用，异常自动回退分桶粗估）；
// Bucket 模式用分桶加权粗估。编码名校验由 RouterOptionsValidator 在启动时完成。
// 统一经 CalibratingTokenEstimator 包装：用上游真实 usage 的 EMA 比率校正系统性偏差
// （分桶对 agent 负载实测偏低 ~34%），RouterEngine/压缩器等所有消费方自动获得校准值。
builder.Services.AddSingleton<CalibratingTokenEstimator>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    ITokenEstimator inner = options.Routing.TokenEstimation == TokenEstimationMode.Bucket
        ? new BucketTokenEstimator()
        : new TiktokenTokenEstimator(options.Routing.TiktokenEncoding);
    return new CalibratingTokenEstimator(inner);
});
builder.Services.AddSingleton<ITokenEstimator>(sp => sp.GetRequiredService<CalibratingTokenEstimator>());

// ── Token 压缩引擎（RTK + Caveman 双引擎串联）──────────────────────
// RTK Shell 输出压缩：工具执行结果（bash/git/npm 输出等）去 ANSI/进度条/空行
builder.Services.AddSingleton<RtkShellOutputCompressor>();
// Caveman 文本精简：英文/中文冗余表达压缩客套话剔除
builder.Services.AddSingleton<CavemanCondenser>();
// 组合压缩流水线：RTK → Caveman → AdaptivePromptPruner 三阶段串联
builder.Services.AddSingleton<TokenCompressionPipeline>();

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

// Thompson 采样 + Contextual Bandit 状态持久化（MariaDB/SQLite 双后端，与成本账本共享连接）。
builder.Services.AddSingleton<IThompsonStateStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    if (!options.Budget.UsePersistentStore)
        return NullLearningStateStore.Instance;
    if (string.Equals(options.Budget.StoreProvider, "MariaDb", StringComparison.OrdinalIgnoreCase))
        return new MariaDbLearningStateStore(options.Budget.MariaDbConnectionString!);
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
    return tsStore is SqliteLearningStateStore or MariaDbLearningStateStore
        ? (IBanditStateStore)tsStore
        : NullLearningStateStore.Instance;
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
builder.Services.AddSingleton<SessionLatencyTracker>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSecurityServices(builder.Configuration);

builder.Services.AddSingleton<IResponseCache>(sp => new MemoryResponseCache(
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<IOptions<RouterOptions>>().Value.Routing.ResponseCacheMaxEntries,
    sp.GetRequiredService<IOptions<RouterOptions>>().Value.Routing.ResponseCacheMaxBytes,
    useSize: true)); // AddMemoryCache 设了 SizeLimit，entry 须申报 Size
// 同一实例的具体类型注册：MaxEntries 在构造时绑定（重启生效），dashboard 状态端点借它读命中/写入统计。
builder.Services.AddSingleton(sp => (MemoryResponseCache)sp.GetRequiredService<IResponseCache>());

builder.Services.AddSingleton<ISemanticResponseCache>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new SemanticResponseCache(options.Routing.SemanticCacheMaxEntries, sp.GetService<ISemanticVectorEngine>());
});

// 分布式状态网格 (Distributed State Mesh)
builder.Services.AddSingleton<OptiRouter.Mesh.IDistributedStateMesh>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string nodeId = options.Routing.MeshNodeId;

    // 配置了 Redis 连接串时使用集群级网格；连接失败降级 InMemory（单机模式），不阻断启动。
    if (!string.IsNullOrWhiteSpace(options.Routing.MeshRedisConnectionString))
    {
        try
        {
            return new OptiRouter.Mesh.RedisDistributedStateMesh(
                new OptiRouter.Mesh.RedisChannelBus(options.Routing.MeshRedisConnectionString),
                nodeId,
                sp.GetService<ILogger<OptiRouter.Mesh.RedisDistributedStateMesh>>());
        }
        catch (Exception ex)
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("OptiRouter.Mesh");
            logger?.LogWarning(ex, "Redis mesh unavailable, falling back to in-memory mesh");
        }
    }

    return new OptiRouter.Mesh.InMemoryDistributedStateMesh(nodeId);
});

builder.Services.AddSingleton<OptiRouter.Mesh.DistributedMeshSynchronizer>(sp =>
{
    var mesh = sp.GetRequiredService<OptiRouter.Mesh.IDistributedStateMesh>();
    var kvTrie = sp.GetService<KvCachePrefixTrie>();
    var kalmanTracker = sp.GetService<KalmanLatencyTracker>();
    var costLedger = sp.GetService<CostLedger>();
    var resilienceEngine = sp.GetService<PredictiveResilienceEngine>();
    var logger = sp.GetService<ILogger<OptiRouter.Mesh.DistributedMeshSynchronizer>>();
    return new OptiRouter.Mesh.DistributedMeshSynchronizer(mesh, kvTrie, kalmanTracker, costLedger, resilienceEngine, logger);
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
builder.Services.AddSingleton<ByzantineConsensusEngine>(sp =>
    new ByzantineConsensusEngine(sp.GetService<ISemanticVectorEngine>()));
builder.Services.AddSingleton<PredictiveResilienceEngine>();
builder.Services.AddSingleton<RagContextDensityAnalyzer>();
builder.Services.AddSingleton<OptiRouter.Mcp.McpToolComplexityAnalyzer>();
builder.Services.AddSingleton<OptiRouter.Mcp.McpToolCallSanitizer>();
builder.Services.AddSingleton<OptiRouter.Mcp.McpToolRegistry>();
builder.Services.AddHttpClient<OptiRouter.Mcp.IMcpToolExecutor, OptiRouter.Mcp.McpToolExecutor>();
builder.Services.AddSingleton<OptiRouter.Mcp.McpToolOrchestrator>(sp =>
    new OptiRouter.Mcp.McpToolOrchestrator(
        sp.GetRequiredService<OptiRouter.Mcp.McpToolRegistry>(),
        sp.GetRequiredService<OptiRouter.Mcp.IMcpToolExecutor>(),
        sp.GetRequiredService<IModelClientProvider>(),
        sp.GetService<ILogger<OptiRouter.Mcp.McpToolOrchestrator>>(),
        recorder: sp.GetRequiredService<OptiRouter.Endpoints.OutcomeRecorder>()));

// ── MCP Server 协议层（HTTP transport，暴露内置工具给外部 MCP 客户端）───────────
// MCP Server：OptiRouter 作为 MCP Server 被外部 agent（Claude Code/Cline/Cursor）调用
builder.Services.AddSingleton(new McpServerOptions
{
    Enabled = true,
    // 注意：Path 变更须同步 RequestPathPolicy.AdminPathPrefixes（/mcp 前缀靠它纳入管理端鉴权）。
    Path = "/mcp",
    MaxToolCallTimeoutMs = 30_000
});
// MCP 内置工具提供者（路由状态/预算/模型健康/工具状态等）
builder.Services.AddSingleton<McpServerToolProvider, OptiRouter.Mcp.OptiRouterMcpTools>();
// MCP Server 主机
builder.Services.AddSingleton<OptiRouter.Mcp.McpServerHost>();
builder.Services.AddSingleton<OptiRouter.Compression.IPromptPruner, OptiRouter.Compression.AdaptivePromptPruner>();
builder.Services.AddSingleton<OptiRouter.Clients.IProviderAdapterSandbox, OptiRouter.Clients.ProviderAdapterSandbox>();
builder.Services.AddSingleton<OptiRouter.Benchmarks.StressBenchmarkEngine>();

// 路由决策与执行引擎注册见 Composition/RoutingEngineServiceExtensions.cs。
builder.Services.AddRoutingDecisionEngine();

// Prometheus 指标集合（单例，ProxyOrchestrator 经 DI 注入）。
// 仪表（Counter/Histogram/Gauge）在 RouterMetrics 构造时向 prometheus-net 静态注册表登记，
// 后台 MetricsGaugeUpdaterService 周期刷新花费/断路器 gauge。
builder.Services.AddSingleton<OptiRouter.Metrics.RouterMetrics>();

// 模型配置服务（配置库，Dashboard 读写，IConfigurationRoot.Reload() 热生效）。
builder.Services.AddSingleton<ModelsConfigService>(sp =>
{
    var store = sp.GetRequiredService<AppConfigDbStore>();
    var configRoot = (IConfigurationRoot)sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ModelsConfigService>>();
    return new ModelsConfigService(store, configRoot, logger);
});

// 后台定时主动探活：启动预热一轮，随后按 HealthProbeIntervalSeconds 周期对所有启用模型探测，
// 结果上报 ModelHealthTracker（成功累计半开/闭合，失败计熔断）。EnableHealthProbe=false 可关闭。
builder.Services.AddHostedService<ModelHealthProbeService>();

// 后台周期聚合模型延迟统计，写入 ILatencyStatsProvider 供 LatencyAwarePolicy 读。
// 复用 HealthProbeIntervalSeconds 周期，避免引入独立定时器。EnableLatencyAware=false 时不聚合。
builder.Services.AddHostedService<LatencyStatsAggregatorService>();

// 审计保留淘汰：按 AuditRetentionHours 周期 EvictBefore，防止 request_audit 无界增长。
builder.Services.AddHostedService<AuditRetentionService>();

// 会话粘性回载：启动时从审计历史恢复每会话最近成功模型——IMemoryCache 粘性重启即清空，
// 活跃 harness 会话跨重启的首个请求会重新抽签换上游，烧掉整个 prompt 缓存前缀。
builder.Services.AddHostedService<SessionAffinityWarmupService>();

// 指标 gauge 刷新服务：周期同步花费/断路器 gauge（复用探活周期，零独立定时器）。
// EnableMetrics=false 时不影响功能，但 gauge 保持零值。
builder.Services.AddHostedService<MetricsGaugeUpdaterService>();

// 告警 Webhook 推送：周期检查 AlertEngine 活跃告警，新增推送 alert、恢复推送 resolved，
// 转换事件同步记入 AlertHistory；未配置 AlertWebhookUrl 时仅记录历史，URL 配置后热生效。
// 10s 超时：webhook 目标无响应时不能让单条推送阻塞默认 100s、告警队列积压。
builder.Services.AddHttpClient("alert-webhook", client => client.Timeout = TimeSpan.FromSeconds(10));

// 模型配置页拉取上游模型列表的短命命名 client：超时 10 秒（用户期望快速失败）。
builder.Services.AddHttpClient("model-discover", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<OptiRouter.Health.AlertWebhookNotifier>(sp =>
    new OptiRouter.Health.AlertWebhookNotifier(
        () => sp.GetRequiredService<AlertEngine>().Check(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("alert-webhook"),
        sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
        sp.GetService<ILogger<OptiRouter.Health.AlertWebhookNotifier>>(),
        sp.GetRequiredService<OptiRouter.Health.AlertHistory>()));

// 内容审核（Moderation）：ConfigurableModerator 每次审核读 IOptionsMonitor 当前值，
// ModerationEndpoint/ApiKey/Threshold 热重载即时生效（与其余 Routing 项一致）。
// 端点未配置时 fail-open（返回非违规）；ProxyOrchestrator 另有 EnableContentModeration 总开关。
builder.Services.AddSingleton<OptiRouter.Compliance.IContentModerator>(sp =>
    new OptiRouter.Compliance.ConfigurableModerator(
        sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetService<ILogger<OptiRouter.Compliance.OpenAIModerationClient>>()));

// 健康检查：验证内部依赖（成本账本 store 连接正常）。
builder.Services.AddHealthChecks()
    .AddCheck<CostLedgerHealthCheck>("cost-ledger", failureStatus: HealthStatus.Unhealthy);

// OpenAPI 契约文档（Swagger）：暴露于 /dashboard/swagger（管理鉴权保护），
// openapi.json 位于 /dashboard/api-docs/v1/openapi.json。
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "OptiRouter API",
        Version = "v1",
        Description = "多模型智能路由代理：OpenAI 兼容 Chat Completions、模型发现、管理与租户用量 API。" +
                      "代理端点使用 Bearer 鉴权（ProxyApiKey 或租户密钥）；管理端点见 /dashboard。"
    });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Bearer <proxy-api-key> 或租户客户端密钥"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    string xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// Blazor Server：组件化 Dashboard + 模型配置 UI。
// _Host.cshtml 是 Razor Page（用 <component render-mode="ServerPrerendered">），
// 需 AddRazorPages 提供 PersistentComponentState 等预渲染服务，否则 AntiforgeryStateProvider 解析失败。
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
// 后台标签页节流治理：Chrome/Edge 对后台 >5 分钟的标签页把 JS 定时器压到 1 次/分钟，
// Blazor 客户端 15s 心跳 ping 实际被拉长到 ~60s，超过默认 ClientTimeoutInterval(30s)
// 即被服务端判死掐断电路——切回 /requests 等管理台页面必现"正在重试连接"横幅。
// 放宽到 90s 容下一个完整节流周期；客户端侧 serverTimeout 不受影响（WS 消息事件
// 不被节流，服务端 ping 到达即处理），无需动前端。全局无其他 SignalR Hub，仅 Blazor 电路。
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o =>
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(90));
// ApiService 必须 Scoped（circuit 内共享）：Blazor Server 页面间 NavLink 导航后保持同一实例。
// Cookie 注入在 ApiService 内部按请求执行（构造时捕获 Cookie 存于 Scoped 实例、HttpContext 可用时刷新）。
// 不用 DelegatingHandler：HttpClientFactory 的 handler 管道跨 circuit 缓存共享，
// 其实例字段（Cookie 缓存/401 跳转标记）会造成多管理员会话间状态串扰。
// 300s：长评测（eval/run 走真实上游管线）可能超过 100s
builder.Services.AddHttpClient(nameof(ApiService), client => client.Timeout = TimeSpan.FromSeconds(300));
builder.Services.AddScoped<ApiService>(sp =>
{
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ApiService));
    var nav = sp.GetRequiredService<NavigationManager>();
    return new ApiService(client, nav, sp.GetService<IHttpContextAccessor>(), sp.GetService<ILogger<ApiService>>());
});
builder.Services.AddScoped<ToastService>();



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
