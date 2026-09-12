using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Compliance;
using OptiRouter.Clients;
using OptiRouter.Concurrency;
using OptiRouter.Mcp;
using OptiRouter.Health;
using OptiRouter.Compression;
using OptiRouter.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Metrics;
using OptiRouter.Routing;

namespace OptiRouter.Composition;

/// <summary>
/// 路由决策与执行引擎的 DI 注册（#4 模块化：组合根拆分——"路由决策/执行"模块）。
/// <para>策略链顺序即路由语义，存在隐式顺序依赖（ModePolicy → ExplicitModelPolicy →
/// 过滤/分类 → 学习/负载策略 → Failover/LoadBalance 收尾），调整前必须通读链内注释。</para>
/// </summary>
internal static class RoutingEngineServiceExtensions
{
    public static IServiceCollection AddRoutingDecisionEngine(this IServiceCollection services)
    {
services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var healthTracker = sp.GetRequiredService<ModelHealthTracker>();
    var tokenEstimator = sp.GetRequiredService<ITokenEstimator>();
    var vectorEngine = sp.GetRequiredService<ISemanticVectorEngine>();
    var tsStore = sp.GetRequiredService<ThompsonStateStore>();
    var kalmanTracker = sp.GetRequiredService<KalmanLatencyTracker>();
    var kvCacheTrie = sp.GetRequiredService<KvCachePrefixTrie>();
    var resilienceEngine = sp.GetRequiredService<PredictiveResilienceEngine>();
    var ragAnalyzer = sp.GetRequiredService<RagContextDensityAnalyzer>();
    var mcpAnalyzer = sp.GetRequiredService<OptiRouter.Mcp.McpToolComplexityAnalyzer>();
    // 策略链在请求处理时读取 IOptionsMonitor.CurrentValue（ProxyOrchestrator 注入），
    // Tier/价格等字段 reload 后立即生效；Models 端点连接配置（BaseUrl/ApiKey/Timeout）
    // 缓存于 ModelClientProvider，经 OnChange 热更新重建（见其注册处）。
    var policies = new List<IRouterPolicy>
    {
        new RoutingModePolicy(),
        // 显式模型固定必须在 ModePolicy 之后执行：若用户指定了模型，则覆盖 ModePolicy 的预设档位。
        new ExplicitModelPolicy(),
        new DataSovereigntyPolicy(),
        new CapabilityFilterPolicy(),
        new RuleClassifierPolicy(),
        new SessionAffinityPolicy(sp.GetRequiredService<IMemoryCache>(), sp.GetRequiredService<SessionLatencyTracker>()),
        new SemanticRouterPolicy(vectorEngine),
        new RagAwareRoutingPolicy(ragAnalyzer),
        new McpToolRoutingPolicy(mcpAnalyzer),
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
services.AddSingleton<OutcomeRecorder>(sp => new OutcomeRecorder(
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
    calibratingEstimator: sp.GetRequiredService<CalibratingTokenEstimator>(),
    kalmanTracker: sp.GetRequiredService<KalmanLatencyTracker>(),
    kvCacheTrie: sp.GetRequiredService<KvCachePrefixTrie>(),
    resilienceEngine: sp.GetRequiredService<PredictiveResilienceEngine>(),
    meshSynchronizer: sp.GetService<OptiRouter.Mesh.DistributedMeshSynchronizer>()));
services.AddSingleton<CascadeUpgradeHandler>();
services.AddSingleton<FusionRouter>();
services.AddSingleton<RaceOrchestrator>();
// 分区并发闸注册表（DI 单例）：租约化并发控制，替代原静态 ConcurrencyRegistry
// （静态实现跨测试宿主共享状态，且返回裸信号量存在淘汰/替换竞态）。
services.AddSingleton<ConcurrencyRegistry>();
// LLM-as-judge 采样质量打分：旁路后台任务，按采样率把成功响应送打分模型并回灌学习状态。
services.AddSingleton<LlmQualityJudge>();
// regenerate 负反馈跟踪器：进程内状态，供 ProxyOrchestrator 在同键请求重发时注入惩罚 reward。
services.AddSingleton<RegenerateFeedbackTracker>();
services.AddSingleton<ProxyOrchestrator>();

        return services;
    }

    /// <summary>
    /// 代理执行支撑：响应/语义缓存、Mesh 同步、自适应并发、流式合规过滤器、
    /// Kalman 延迟、KV 前缀缓存、推理控制、拜占庭共识、预测韧性、RAG/MCP 分析、
    /// MCP 工具执行与 Server 宿主、提示压缩、适配器沙箱、压力基准。
    /// </summary>
    public static IServiceCollection AddProxySupportServices(this IServiceCollection services)
    {
services.AddSingleton<IResponseCache>(sp => new MemoryResponseCache(
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<IOptions<RouterOptions>>().Value.Routing.ResponseCacheMaxEntries,
    sp.GetRequiredService<IOptions<RouterOptions>>().Value.Routing.ResponseCacheMaxBytes,
    useSize: true)); // AddMemoryCache 设了 SizeLimit，entry 须申报 Size
// 同一实例的具体类型注册：MaxEntries 在构造时绑定（重启生效），dashboard 状态端点借它读命中/写入统计。
services.AddSingleton(sp => (MemoryResponseCache)sp.GetRequiredService<IResponseCache>());

services.AddSingleton<ISemanticResponseCache>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new SemanticResponseCache(options.Routing.SemanticCacheMaxEntries, sp.GetService<ISemanticVectorEngine>());
});

// 分布式状态网格 (Distributed State Mesh)
services.AddSingleton<OptiRouter.Mesh.IDistributedStateMesh>(sp =>
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

services.AddSingleton<OptiRouter.Mesh.DistributedMeshSynchronizer>(sp =>
{
    var mesh = sp.GetRequiredService<OptiRouter.Mesh.IDistributedStateMesh>();
    var kvTrie = sp.GetService<KvCachePrefixTrie>();
    var kalmanTracker = sp.GetService<KalmanLatencyTracker>();
    var costLedger = sp.GetService<CostLedger>();
    var resilienceEngine = sp.GetService<PredictiveResilienceEngine>();
    var logger = sp.GetService<ILogger<OptiRouter.Mesh.DistributedMeshSynchronizer>>();
    return new OptiRouter.Mesh.DistributedMeshSynchronizer(mesh, kvTrie, kalmanTracker, costLedger, resilienceEngine, logger);
});

services.AddSingleton<IAdaptiveConcurrencyLimiter>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new AdaptiveConcurrencyLimiter(options.Routing.AdaptiveMinLimit, options.Routing.AdaptiveMaxLimit);
});

services.AddSingleton<IStreamingComplianceFilter>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new StreamingSlidingWindowFilter(options.Routing);
});

services.AddSingleton<KalmanLatencyTracker>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new KalmanLatencyTracker(
        targetLatencyMs: options.Routing.KalmanTargetLatencyMs,
        penaltyGamma: options.Routing.KalmanPenaltyGamma);
});

services.AddSingleton<KvCachePrefixTrie>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    return new KvCachePrefixTrie(TimeSpan.FromMinutes(options.Routing.KvCacheTtlMinutes));
});

services.AddSingleton<ReasoningEffortController>();
services.AddSingleton<ByzantineConsensusEngine>(sp =>
    new ByzantineConsensusEngine(sp.GetService<ISemanticVectorEngine>()));
services.AddSingleton<PredictiveResilienceEngine>();
services.AddSingleton<RagContextDensityAnalyzer>();
services.AddSingleton<OptiRouter.Mcp.McpToolComplexityAnalyzer>();
services.AddSingleton<OptiRouter.Mcp.McpToolCallSanitizer>();
services.AddSingleton<OptiRouter.Mcp.McpToolRegistry>();
services.AddHttpClient<OptiRouter.Mcp.IMcpToolExecutor, OptiRouter.Mcp.McpToolExecutor>();
services.AddSingleton<OptiRouter.Mcp.McpToolOrchestrator>(sp =>
    new OptiRouter.Mcp.McpToolOrchestrator(
        sp.GetRequiredService<OptiRouter.Mcp.McpToolRegistry>(),
        sp.GetRequiredService<OptiRouter.Mcp.IMcpToolExecutor>(),
        sp.GetRequiredService<IModelClientProvider>(),
        sp.GetService<ILogger<OptiRouter.Mcp.McpToolOrchestrator>>(),
        recorder: sp.GetRequiredService<OptiRouter.Endpoints.OutcomeRecorder>()));

// ── MCP Server 协议层（HTTP transport，暴露内置工具给外部 MCP 客户端）───────────
// MCP Server：OptiRouter 作为 MCP Server 被外部 agent（Claude Code/Cline/Cursor）调用
services.AddSingleton(new McpServerOptions
{
    Enabled = true,
    // 注意：Path 变更须同步 RequestPathPolicy.AdminPathPrefixes（/mcp 前缀靠它纳入管理端鉴权）。
    Path = "/mcp",
    MaxToolCallTimeoutMs = 30_000
});
// MCP 内置工具提供者（路由状态/预算/模型健康/工具状态等）
services.AddSingleton<McpServerToolProvider, OptiRouter.Mcp.OptiRouterMcpTools>();
// MCP Server 主机
services.AddSingleton<OptiRouter.Mcp.McpServerHost>();
services.AddSingleton<OptiRouter.Compression.IPromptPruner, OptiRouter.Compression.AdaptivePromptPruner>();
services.AddSingleton<OptiRouter.Clients.IProviderAdapterSandbox, OptiRouter.Clients.ProviderAdapterSandbox>();
services.AddSingleton<OptiRouter.Benchmarks.StressBenchmarkEngine>();

        return services;
    }
}
