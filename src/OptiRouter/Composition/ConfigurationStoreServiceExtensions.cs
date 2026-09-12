using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Compression;
using OptiRouter.Configuration;
using OptiRouter.Concurrency;
using OptiRouter.Endpoints;
using OptiRouter.Health;
using OptiRouter.Metrics;
using OptiRouter.Mcp;
using OptiRouter.Routing;

namespace OptiRouter.Composition;

/// <summary>
/// 配置存储与租户/学习状态支撑的 DI 注册（#4 模块化：组合根拆分——"配置存储"模块）。
/// </summary>
internal static class ConfigurationStoreServiceExtensions
{
    /// <summary>
    /// 配置库（SQLite/MariaDB Auto 切换）与管理端密钥存储；并挂接 DbAppConfigSource
    /// 使配置库成为 IConfiguration 的热重载源（须在其他配置源之后、Build 之前调用）。
    /// </summary>
    public static IServiceCollection AddConfigurationStores(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfigurationRoot configuration,
        string configDbPath,
        string? configDbConnectionString)
    {
// 配置存储：Routing/Budget/模型配置全部落配置库（StoreProvider 默认 Auto：有 ConfigDbConnectionString 即 MariaDB，否则 SQLite）。
// SQLite 后端可由 ConfigDbPath 指定文件路径（默认 data/optirouter-config.db）。
// 页面写入经 AppConfigDbStore → IConfigurationRoot.Reload() 热生效。
// 首启迁移：库为空时从 appsettings.json 的 Routing/Budget 段与遗留 models-config.json 导入一次，
// 之后 DB 为唯一权威，appsettings.json 仅保留部署级设置（密钥/端口/限流/DB 连接）。
services.AddSingleton(sp => new AppConfigDbStore(configDbPath, configDbConnectionString));
// 管理端密钥：哈希存配置库 security scope（构造时种子；appsettings 仅首启种子源）。
services.AddSingleton(sp => new OptiRouter.Configuration.AdminKeyStore(
    sp.GetRequiredService<AppConfigDbStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetService<ILogger<OptiRouter.Configuration.AdminKeyStore>>()));
        return services;
    }

    /// <summary>
    /// 租户密钥、成本账本、模型健康、学习状态（Thompson/Bandit）、估算器、压缩、
    /// 语义向量与提示缓存亲和——路由决策与计费的状态基座。
    /// </summary>
    public static IServiceCollection AddTenantAndLearningServices(
        this IServiceCollection services,
        string contentRootPath,
        string? configDbConnectionString)
    {
services.AddSingleton<ClientKeyService>(sp => new ClientKeyService(
    Path.Combine(contentRootPath, "data", "client-keys.json"),
    sp.GetRequiredService<ILogger<ClientKeyService>>(),
    mariaDbConnectionString: configDbConnectionString,
    alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>()));

services.AddSingleton<CostLedger>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    var store = sp.GetRequiredService<ICostLedgerStore>();
    return new CostLedger(store, options.Budget.SessionEvictionHours);
});
services.AddSingleton<ModelHealthTracker>(sp =>
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
services.AddSingleton<ILatencyStatsProvider, LatencyStatsCache>();

// 告警引擎：检查预算、断路器、失败率等条件。
services.AddSingleton<AlertEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var auditStore = sp.GetRequiredService<IRequestAuditStore>();
    var routerOptions = sp.GetRequiredService<IOptionsMonitor<RouterOptions>>();
    return new AlertEngine(ledger, healthTracker, auditStore, routerOptions);
});

// 告警历史环形缓冲：告警出现/恢复事件进程内留痕，供 Dashboard 历史查询。
services.AddSingleton<OptiRouter.Health.AlertHistory>();

// 最近探活结果留痕：手动 + 后台探活统一写入，模型配置页"连通状态"列刷新后预填。
services.AddSingleton<OptiRouter.Health.ProbeResultStore>();

// 审计分析：时间窗全量聚合报告（总览/分模型/分档/级联/Fusion/路由原因/日趋势），供策略调优闭环。
services.AddSingleton<AuditAnalysisService>();

// Token 估算器：Tiktoken 模式用 SharpToken 真实 BPE 计数（内置词表、离线可用，异常自动回退分桶粗估）；
// Bucket 模式用分桶加权粗估。编码名校验由 RouterOptionsValidator 在启动时完成。
// 统一经 CalibratingTokenEstimator 包装：用上游真实 usage 的 EMA 比率校正系统性偏差
// （分桶对 agent 负载实测偏低 ~34%），RouterEngine/压缩器等所有消费方自动获得校准值。
services.AddSingleton<CalibratingTokenEstimator>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    ITokenEstimator inner = options.Routing.TokenEstimation == TokenEstimationMode.Bucket
        ? new BucketTokenEstimator()
        : new TiktokenTokenEstimator(options.Routing.TiktokenEncoding);
    return new CalibratingTokenEstimator(inner);
});
services.AddSingleton<ITokenEstimator>(sp => sp.GetRequiredService<CalibratingTokenEstimator>());

// ── Token 压缩引擎（RTK + Caveman 双引擎串联）──────────────────────
// RTK Shell 输出压缩：工具执行结果（bash/git/npm 输出等）去 ANSI/进度条/空行
services.AddSingleton<RtkShellOutputCompressor>();
// Caveman 文本精简：英文/中文冗余表达压缩客套话剔除
services.AddSingleton<CavemanCondenser>();
// 组合压缩流水线：RTK → Caveman → AdaptivePromptPruner 三阶段串联
services.AddSingleton<TokenCompressionPipeline>();

services.AddSingleton<ISemanticVectorEngine>(sp =>
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
services.AddSingleton<IThompsonStateStore>(sp =>
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
services.AddSingleton<IBanditStateStore>(sp =>
{
    var tsStore = sp.GetRequiredService<IThompsonStateStore>();
    return tsStore is SqliteLearningStateStore or MariaDbLearningStateStore
        ? (IBanditStateStore)tsStore
        : NullLearningStateStore.Instance;
});

services.AddSingleton<ThompsonStateStore>(sp =>
{
    var persistence = sp.GetRequiredService<IThompsonStateStore>();
    var logger = sp.GetRequiredService<ILogger<ThompsonStateStore>>();
    return new ThompsonStateStore(persistence, logger);
});
services.AddSingleton<ContextualBanditState>(sp =>
{
    var persistence = sp.GetRequiredService<IBanditStateStore>();
    var logger = sp.GetRequiredService<ILogger<ContextualBanditState>>();
    return new ContextualBanditState(persistence: persistence, logger: logger);
});
services.AddSingleton<UpstreamQuotaStateStore>();
services.AddSingleton<PromptCacheAffinityStore>();
services.AddSingleton<FusionPanelSelector>();
services.AddSingleton<SessionLatencyTracker>();
services.AddHttpContextAccessor();

        return services;
    }
}
