using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 启动时从审计历史回载每会话最近成功模型到会话粘性缓存，恢复重启丢失的粘性。
/// <para>
/// 会话粘性存于 IMemoryCache（进程内），重启即清空——活跃 harness 会话跨重启后的
/// 首个请求会重新抽签，切到另一上游即烧掉整个 prompt 缓存前缀（实测 116K 全量 miss、
/// 会话缓存命中率从 99% 掉到 54%）。审计表自带"每会话最近成功模型"，回载后粘性即刻接续。
/// </para>
/// </summary>
/// <remarks>
/// intentional-simple: 拉最近 2000 条审计、进程内取每会话最新一条成功记录，仅启动执行一次；
/// 回载条目带常规粘性 TTL，活跃会话随后续成功自然续期。回载的模型若已熔断/被过滤，
/// <see cref="SessionAffinityPolicy"/> 照常降级不强推，无额外校验必要。
/// </remarks>
public sealed class SessionAffinityWarmupService : BackgroundService
{
    private const int LoadLimit = 2000;

    private readonly IRequestAuditStore _auditStore;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly ILogger<SessionAffinityWarmupService> _logger;

    public SessionAffinityWarmupService(
        IRequestAuditStore auditStore,
        IMemoryCache cache,
        IOptionsMonitor<RouterOptions> options,
        ILogger<SessionAffinityWarmupService> logger)
    {
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 等审计存储与配置装载就绪（持久后端连接池惰性初始化），再回载；
            // 回载失败不影响服务运行——粘性会随流量自然重建，只是重启窗口内命中率下降。
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            int restored = RestoreFromAudit();
            if (restored > 0)
            {
                _logger.LogInformation(
                    "Session affinity warmup: restored {Count} session bindings from audit history",
                    restored);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session affinity warmup failed; sticky routing will rebuild from live traffic");
        }
    }

    /// <summary>从最近审计中回载每会话最新成功模型到粘性缓存，返回回载条数。</summary>
    public int RestoreFromAudit()
    {
        var routing = _options.CurrentValue.Routing;
        if (!routing.EnableSessionAffinity) return 0;
        int ttl = routing.SessionAffinityTtlSeconds > 0 ? routing.SessionAffinityTtlSeconds : 600;

        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        int restored = 0;
        foreach (var record in _auditStore.GetRecent(LoadLimit))
        {
            // GetRecent 最新在前：每会话首条成功记录即最近一次成功模型。
            if (!record.Success || string.IsNullOrEmpty(record.SessionId) || !seenSessions.Add(record.SessionId))
                continue;

            _cache.Set(
                SessionAffinityPolicy.CacheKeyPrefix + record.SessionId,
                new AffinityRecord(record.Model, now),
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl),
                    Size = 1
                });
            restored++;
        }
        return restored;
    }
}
