using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 后台定时主动探活所有启用模型，避免熔断恢复纯靠真实流量半开探测。
/// <para>
/// 启动时先预热一轮（检测配置可达性，失败计入断路器），随后按
/// <see cref="RoutingOptions.HealthProbeIntervalSeconds"/> 周期探活。
/// 探活结果上报 <see cref="ModelHealthTracker"/>：成功调 <see cref="ModelHealthTracker.RecordSuccess"/>，
/// 失败调 <see cref="ModelHealthTracker.RecordFailure"/>（受 <c>FailoverFailureThreshold</c> 约束触发熔断）。
/// </para>
/// <para>
/// intentional-simple: 探活串行执行（模型数通常 &lt;10），并发探活收益低且放大上游压力。
/// 单次探活失败不抛——仅记录并上报，服务持续运行。
/// </para>
/// </summary>
public sealed class ModelHealthProbeService : BackgroundService
{
    private readonly IModelClientProvider _clientProvider;
    private readonly ModelHealthTracker _healthTracker;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly UpstreamQuotaStateStore _quotaStore;
    private readonly ILatencyStatsProvider? _latencyStats;
    private readonly ILogger<ModelHealthProbeService> _logger;

    /// <summary>
    /// 初始化探活服务。
    /// </summary>
    /// <param name="clientProvider">模型客户端提供者，用于按端点取客户端发探测。</param>
    /// <param name="healthTracker">跨请求模型健康跟踪器，探活结果上报至此。</param>
    /// <param name="quotaStore">进程内上游配额状态。</param>
    /// <param name="options">路由配置监视器，读取探活开关/间隔/熔断参数（支持 reload）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="latencyStats">延迟统计快照（可选），按模型平均 TTFT 自适应放宽探活超时。</param>
    public ModelHealthProbeService(
        IModelClientProvider clientProvider,
        ModelHealthTracker healthTracker,
        UpstreamQuotaStateStore quotaStore,
        IOptionsMonitor<RouterOptions> options,
        ILogger<ModelHealthProbeService> logger,
        ILatencyStatsProvider? latencyStats = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _healthTracker = healthTracker ?? throw new ArgumentNullException(nameof(healthTracker));
        _quotaStore = quotaStore ?? throw new ArgumentNullException(nameof(quotaStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _latencyStats = latencyStats;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        // 启动预热：不等首个周期，立即探活一轮，提前暴露配置/网络问题。
        // 未启用时跳过预热但进入循环——reload 中途开启 EnableHealthProbe 仍可生效（对齐 LatencyStatsAggregatorService）。
        if (options.Routing.EnableHealthProbe)
            await ProbeAllAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            int interval = Math.Max(10, options.Routing.HealthProbeIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            options = _options.CurrentValue;
            if (!options.Routing.EnableHealthProbe)
                continue;

            await ProbeAllAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;
        int requiredSuccesses = options.Routing.FailoverHalfOpenRequiredSuccesses;

        var freshWindow = TimeSpan.FromSeconds(Math.Max(0, options.Routing.HealthProbeFreshSuccessSkipSeconds));

        foreach (var endpoint in options.Models.Where(m => m.Enabled))
        {
            if (cancellationToken.IsCancellationRequested) return;

            // 新鲜成功门控：真实流量（或上次探活）近期成功过的模型跳过主动探活。
            // 活跃模型由真实流量背书健康，探活只会重复计费并引入误判
            // （实测：探活 401/固定 5s 超时曾把真实请求正常的模型反复熔断）。
            if (_healthTracker.HasRecentSuccess(endpoint.Name, freshWindow))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Health probe skipped (recent success): {Name}", endpoint.Name);
                continue;
            }

            try
            {
                var client = _clientProvider.GetClient(endpoint);
                var result = await client.ProbeAsync(cancellationToken, ComputeProbeTimeout(options, endpoint.Name)).ConfigureAwait(false);

                if (result.Healthy)
                {
                    // 主动探活未经 TryBeginProbe 放行：releaseProbe:false，不消耗半开探测槽位
                    _healthTracker.RecordSuccess(endpoint.Name, requiredSuccesses, releaseProbe: false);
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Health probe OK: {Name} ({Ms}ms)", endpoint.Name, result.LatencyMs);
                }
                else
                {
                    if (result.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _quotaStore.Record(endpoint.Name, result.Metadata, rateLimited: true);
                        _logger.LogWarning("Health probe quota limited: {Name} (status {Status})",
                            endpoint.Name, 429);
                        continue;
                    }
                    bool tripped = _healthTracker.RecordFailure(endpoint.Name, threshold, cooldown, releaseProbe: false);
                    _logger.LogWarning("Health probe FAILED: {Name} ({Reason}){Tripped}",
                        endpoint.Name, result.Error ?? "unknown", tripped ? " (circuit tripped)" : "");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 关停/取消不是健康信号：不计失败、不熔断（曾把关停噪声记成失败，
                // 熔断健康模型并连锁触发 EventLog 关停竞态刷屏）。
                return;
            }
            catch (Exception ex)
            {
                bool tripped = _healthTracker.RecordFailure(endpoint.Name, threshold, cooldown, releaseProbe: false);
                _logger.LogWarning(ex, "Health probe threw for {Name}{Tripped}",
                    endpoint.Name, tripped ? " (circuit tripped)" : "");
            }
        }
    }

    /// <summary>
    /// 探活超时：基准 <see cref="RoutingOptions.HealthProbeTimeoutSeconds"/>，
    /// 有延迟统计时按平均 TTFT 放宽（TTFT×1.5 + 2s），夹在 [5s, 60s]。
    /// 慢首 token 模型（TTFT 40s+）用固定 5s 探活必然超时误熔断。
    /// </summary>
    private TimeSpan ComputeProbeTimeout(RouterOptions options, string modelName)
    {
        double baselineMs = Math.Max(5, options.Routing.HealthProbeTimeoutSeconds) * 1000.0;
        double? avgTtftMs = _latencyStats?.GetStats(modelName)?.AverageTtftMs;
        if (avgTtftMs is > 0)
            baselineMs = Math.Max(baselineMs, avgTtftMs.Value * 1.5 + 2000);

        return TimeSpan.FromMilliseconds(Math.Min(baselineMs, 60_000));
    }
}
