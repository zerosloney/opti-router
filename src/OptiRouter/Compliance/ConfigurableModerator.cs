using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Compliance;

/// <summary>
/// 配置驱动的审核器包装：每次审核读取 <see cref="IOptionsMonitor{RouterOptions}"/> 当前值，
/// ModerationEndpoint/ApiKey/Threshold 热重载即时生效（其余 Routing 开关本就热生效，
/// 旧的单例启动快照是唯一例外）。端点未配置时 fail-open 返回非违规。
/// </summary>
public sealed class ConfigurableModerator : IContentModerator
{
    private readonly IOptionsMonitor<RouterOptions> _monitor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAIModerationClient>? _logger;

    private readonly object _gate = new();
    private OpenAIModerationClient? _inner;
    private string? _cachedEndpoint;
    private string? _cachedApiKey;
    private double _cachedThreshold;

    /// <summary>
    /// 初始化包装器。
    /// </summary>
    public ConfigurableModerator(
        IOptionsMonitor<RouterOptions> monitor,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAIModerationClient>? logger = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "configurable-moderation";

    /// <inheritdoc />
    public Task<ModerationResult> ModerateTextAsync(string text, ModerationDirection direction, CancellationToken ct = default)
    {
        var routing = _monitor.CurrentValue.Routing;
        if (string.IsNullOrWhiteSpace(routing.ModerationEndpoint))
        {
            // 未配置端点：fail-open（调用方另有 EnableContentModeration 总开关）
            return Task.FromResult(new ModerationResult(false, null, 0, "moderation endpoint not configured"));
        }

        var client = GetOrCreateClient(routing);
        return client.ModerateTextAsync(text, direction, ct);
    }

    private OpenAIModerationClient GetOrCreateClient(RoutingOptions routing)
    {
        lock (_gate)
        {
            if (_inner is not null
                && string.Equals(_cachedEndpoint, routing.ModerationEndpoint, StringComparison.Ordinal)
                && string.Equals(_cachedApiKey, routing.ModerationApiKey, StringComparison.Ordinal)
                && _cachedThreshold == routing.ModerationThreshold)
            {
                return _inner;
            }

            _inner = new OpenAIModerationClient(
                _httpClientFactory.CreateClient("moderation"),
                routing.ModerationEndpoint!,
                routing.ModerationApiKey,
                routing.ModerationThreshold,
                _logger);
            _cachedEndpoint = routing.ModerationEndpoint;
            _cachedApiKey = routing.ModerationApiKey;
            _cachedThreshold = routing.ModerationThreshold;
            return _inner;
        }
    }
}
