using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Compliance;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Metrics;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Routing;
using System.Threading.RateLimiting;
namespace OptiRouter.Composition;

/// <summary>
/// 可观测与后台任务的 DI 注册（#4 模块化：组合根拆分）——Prometheus 指标、
/// 模型配置服务、六个后台 HostedService、告警 webhook、内容审核器。
/// </summary>
internal static class ObservabilityServiceExtensions
{
    public static IServiceCollection AddObservabilityAndBackgroundServices(this IServiceCollection services, IConfiguration configuration)
    {
// Prometheus 指标集合（单例，ProxyOrchestrator 经 DI 注入）。
// 仪表（Counter/Histogram/Gauge）在 RouterMetrics 构造时向 prometheus-net 静态注册表登记，
// 后台 MetricsGaugeUpdaterService 周期刷新花费/断路器 gauge。
services.AddSingleton<OptiRouter.Metrics.RouterMetrics>();

// 模型配置服务（配置库，Dashboard 读写，IConfigurationRoot.Reload() 热生效）。
services.AddSingleton<ModelsConfigService>(sp =>
{
    var store = sp.GetRequiredService<AppConfigDbStore>();
    var configRoot = (IConfigurationRoot)sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ModelsConfigService>>();
    return new ModelsConfigService(store, configRoot, logger);
});

// 后台定时主动探活：启动预热一轮，随后按 HealthProbeIntervalSeconds 周期对所有启用模型探测，
// 结果上报 ModelHealthTracker（成功累计半开/闭合，失败计熔断）。EnableHealthProbe=false 可关闭。
services.AddHostedService<ModelHealthProbeService>();

// 后台周期聚合模型延迟统计，写入 ILatencyStatsProvider 供 LatencyAwarePolicy 读。
// 复用 HealthProbeIntervalSeconds 周期，避免引入独立定时器。EnableLatencyAware=false 时不聚合。
services.AddHostedService<LatencyStatsAggregatorService>();

// 审计保留淘汰：按 AuditRetentionHours 周期 EvictBefore，防止 request_audit 无界增长。
services.AddHostedService<AuditRetentionService>();

// 会话粘性回载：启动时从审计历史恢复每会话最近成功模型——IMemoryCache 粘性重启即清空，
// 活跃 harness 会话跨重启的首个请求会重新抽签换上游，烧掉整个 prompt 缓存前缀。
services.AddHostedService<SessionAffinityWarmupService>();

// 指标 gauge 刷新服务：周期同步花费/断路器 gauge（复用探活周期，零独立定时器）。
// EnableMetrics=false 时不影响功能，但 gauge 保持零值。
services.AddHostedService<MetricsGaugeUpdaterService>();

// 告警 Webhook 推送：周期检查 AlertEngine 活跃告警，新增推送 alert、恢复推送 resolved，
// 转换事件同步记入 AlertHistory；未配置 AlertWebhookUrl 时仅记录历史，URL 配置后热生效。
// 10s 超时：webhook 目标无响应时不能让单条推送阻塞默认 100s、告警队列积压。
services.AddHttpClient("alert-webhook", client => client.Timeout = TimeSpan.FromSeconds(10));

// 模型配置页拉取上游模型列表的短命命名 client：超时 10 秒（用户期望快速失败）。
services.AddHttpClient("model-discover", client => client.Timeout = TimeSpan.FromSeconds(10));
services.AddHostedService<OptiRouter.Health.AlertWebhookNotifier>(sp =>
    new OptiRouter.Health.AlertWebhookNotifier(
        () => sp.GetRequiredService<AlertEngine>().Check(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("alert-webhook"),
        sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
        sp.GetService<ILogger<OptiRouter.Health.AlertWebhookNotifier>>(),
        sp.GetRequiredService<OptiRouter.Health.AlertHistory>()));

// 内容审核（Moderation）：ConfigurableModerator 每次审核读 IOptionsMonitor 当前值，
// ModerationEndpoint/ApiKey/Threshold 热重载即时生效（与其余 Routing 项一致）。
// 端点未配置时 fail-open（返回非违规）；ProxyOrchestrator 另有 EnableContentModeration 总开关。
services.AddSingleton<OptiRouter.Compliance.IContentModerator>(sp =>
    new OptiRouter.Compliance.ConfigurableModerator(
        sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetService<ILogger<OptiRouter.Compliance.OpenAIModerationClient>>()));

// 健康检查：验证内部依赖（成本账本 store 连接正常）。
services.AddHealthChecks()
    .AddCheck<CostLedgerHealthCheck>("cost-ledger", failureStatus: HealthStatus.Unhealthy);


        return services;
    }
}
