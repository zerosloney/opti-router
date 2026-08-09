using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Metrics;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 后台周期刷新聚合型 gauge（花费、断路器失败计数）。
/// <para>
/// intentional-simple: 复用 <see cref="RoutingOptions.HealthProbeIntervalSeconds"/> 周期，
/// 与探活/延迟聚合对齐，不引入独立定时器。单次刷新失败不抛——仅记录，服务持续运行。
/// </para>
/// <para>
/// Counter 型指标（requests_total/tokens_total/cost_usd_total/请求延迟）由
/// <see cref="RouterMetrics.RecordAttempt"/> 在请求路径即时记录，无需后台聚合。
/// 仅 gauge（瞬时状态）需要周期同步，避免 scrape 到陈旧值。
/// </para>
/// </summary>
public sealed class MetricsGaugeUpdaterService : BackgroundService
{
    private readonly RouterMetrics _metrics;
    private readonly CostLedger _ledger;
    private readonly ModelHealthTracker _healthTracker;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ILogger<MetricsGaugeUpdaterService> _logger;

    /// <summary>
    /// 初始化 gauge 刷新服务。
    /// </summary>
    /// <param name="metrics">指标集合。</param>
    /// <param name="ledger">成本账本。</param>
    /// <param name="healthTracker">模型健康跟踪器。</param>
    /// <param name="options">路由配置监视器，读取刷新周期（支持 reload）。</param>
    /// <param name="logger">日志记录器。</param>
    public MetricsGaugeUpdaterService(
        RouterMetrics metrics,
        CostLedger ledger,
        ModelHealthTracker healthTracker,
        IOptionsMonitor<RouterOptions> options,
        ILogger<MetricsGaugeUpdaterService> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _healthTracker = healthTracker ?? throw new ArgumentNullException(nameof(healthTracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动即刷新一轮，避免冷启动期间 scrape 到零值。
        Refresh();

        while (!stoppingToken.IsCancellationRequested)
        {
            int interval = Math.Max(10, _options.CurrentValue.Routing.HealthProbeIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Refresh();
        }
    }

    private void Refresh()
    {
        try
        {
            _metrics.RefreshStateGauges(_ledger, _healthTracker);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metrics gauge refresh failed");
        }
    }
}
