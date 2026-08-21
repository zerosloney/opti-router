using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 后台周期淘汰过期审计记录，调用 <see cref="IRequestAuditStore.EvictBefore"/>。
/// <para>
/// request_audit 无自动清理——不淘汰则 AlertEngine 聚合查询与延迟统计随表增长越来越慢。
/// 本服务按 <see cref="RoutingOptions.AuditRetentionHours"/> 保留窗口，周期与 LatencyStatsAggregatorService
/// 对齐（复用 <see cref="RoutingOptions.HealthProbeIntervalSeconds"/>），避免引入独立周期配置。
/// </para>
/// <para>
/// 单次淘汰失败不抛——仅记录，服务持续运行（下一周期重试）。
/// </para>
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    private readonly IRequestAuditStore _auditStore;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ILogger<AuditRetentionService> _logger;

    /// <summary>
    /// 初始化审计保留服务。
    /// </summary>
    /// <param name="auditStore">审计存储，EvictBefore I/O 目标。</param>
    /// <param name="options">路由配置监视器，读取保留时长/周期（支持 reload）。</param>
    /// <param name="logger">日志记录器。</param>
    public AuditRetentionService(
        IRequestAuditStore auditStore,
        IOptionsMonitor<RouterOptions> options,
        ILogger<AuditRetentionService> logger)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            try
            {
                // 周期与 LatencyStatsAggregatorService 对齐（复用探活间隔，最少 10s）。
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.Routing.HealthProbeIntervalSeconds)),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                options = _options.CurrentValue;
                // 0 = 永久保留：跳过淘汰（AddHours 负大值还会溢出 DateTime 范围）。
                if (options.Routing.AuditRetentionHours <= 0)
                    continue;

                DateTime cutoff = DateTime.UtcNow.AddHours(-options.Routing.AuditRetentionHours);
                int evicted = _auditStore.EvictBefore(cutoff);

                if (evicted > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Audit retention: evicted {Count} records older than {Cutoff:O} (retention={Hours}h)",
                        evicted, cutoff, options.Routing.AuditRetentionHours);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 淘汰失败不应影响请求路径。下次周期重试。
                _logger.LogWarning(ex, "Audit retention eviction failed");
            }
        }
    }
}
