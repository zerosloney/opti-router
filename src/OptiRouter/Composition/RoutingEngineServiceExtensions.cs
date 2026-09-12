using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Concurrency;
using OptiRouter.Health;
using OptiRouter.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
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
}
