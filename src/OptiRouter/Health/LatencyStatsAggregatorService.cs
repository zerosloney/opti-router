using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 后台周期聚合模型延迟统计，写入 <see cref="ILatencyStatsProvider"/>。
/// <para>
/// 延迟感知路由策略读内存快照而非每次查审计表——直接查 <c>SqliteRequestAuditStore</c> 会阻塞
/// <c>Append</c> 写入路径（全表锁）。本服务承担 I/O，策略零 I/O。
/// </para>
/// <para>
/// intentional-simple: 聚合周期与探活对齐（复用 <see cref="RoutingOptions.HealthProbeIntervalSeconds"/>），
/// 避免再引入独立周期配置。统计窗口由 <see cref="RoutingOptions.LatencyStatsWindowMinutes"/> 控制。
/// 单次聚合失败不抛——仅记录，服务持续运行。
/// </para>
/// </summary>
public sealed class LatencyStatsAggregatorService : BackgroundService
{
    private readonly IRequestAuditStore _auditStore;
    private readonly ILatencyStatsProvider _statsProvider;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ILogger<LatencyStatsAggregatorService> _logger;

    /// <summary>
    /// 初始化聚合服务。
    /// </summary>
    /// <param name="auditStore">审计存储，聚合 I/O 源。</param>
    /// <param name="statsProvider">延迟快照缓存，聚合结果写入此处。</param>
    /// <param name="options">路由配置监视器，读取窗口/周期（支持 reload）。</param>
    /// <param name="logger">日志记录器。</param>
    public LatencyStatsAggregatorService(
        IRequestAuditStore auditStore,
        ILatencyStatsProvider statsProvider,
        IOptionsMonitor<RouterOptions> options,
        ILogger<LatencyStatsAggregatorService> logger)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _statsProvider = statsProvider ?? throw new ArgumentNullException(nameof(statsProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        // 启动预热：若启用立即聚合一轮，避免首个请求无延迟数据。
        if (options.Routing.EnableLatencyAware)
            await AggregateAsync(stoppingToken).ConfigureAwait(false);

        // 延迟感知未启用时仅跳过聚合，循环持续运行——这样才能观测到经 reload 中途开启 EnableLatencyAware。
        // intentional-simple: 空闲时每周期一次 Delay，开销可忽略；换来 reload 生效性。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 周期/启用状态在延迟期间由 optionsMonitor reload，下一轮读取最新值。
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, options.Routing.HealthProbeIntervalSeconds)),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            options = _options.CurrentValue;
            if (!options.Routing.EnableLatencyAware)
                continue;

            await AggregateAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private Task AggregateAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        int windowMinutes = options.Routing.LatencyStatsWindowMinutes > 0
            ? options.Routing.LatencyStatsWindowMinutes
            : 60;
        DateTime since = DateTime.UtcNow.AddMinutes(-windowMinutes);

        try
        {
            // 后台聚合是 I/O，但发生在请求路径之外。审计表锁与 Append 共享，聚合周期低频，竞争可忽略。
            var raw = _auditStore.GetLatencyStatsSince(since);
            var stats = new Dictionary<string, ModelLatencyStats>(raw.Count, StringComparer.Ordinal);
            foreach (var (model, (avg, n)) in raw)
            {
                stats[model] = new ModelLatencyStats(avg, n);
            }
            _statsProvider.Update(stats);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Latency stats aggregated: {Count} models, window={Window}min",
                    stats.Count, windowMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 聚合失败不应影响请求路径。下次周期重试。
            _logger.LogWarning(ex, "Latency stats aggregation failed");
        }

        return Task.CompletedTask;
    }
}
