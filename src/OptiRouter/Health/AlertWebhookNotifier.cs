using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 告警 Webhook 推送服务 (Alert Webhook Notifier)。
/// 周期性调用 <see cref="AlertEngine.Check"/> 对比活跃告警集合：
/// 新出现的告警推送 <c>alert</c> 事件，消失的告警推送 <c>resolved</c> 事件（含恢复提示），
/// 同一告警去重（按 AlertRecord.Id）不会重复推送；推送失败保留队首下周期重试。
/// 未配置 <see cref="RoutingOptions.AlertWebhookUrl"/> 时静默禁用。
/// </summary>
public sealed class AlertWebhookNotifier : BackgroundService
{
    private readonly Func<IReadOnlyList<AlertRecord>> _checkAlerts;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AlertWebhookNotifier> _logger;
    private readonly IOptionsMonitor<RouterOptions> _options;

    private readonly Queue<(AlertRecord Alert, bool IsResolved)> _pending = new();
    private HashSet<string> _activeAlertIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastMessages = new(StringComparer.Ordinal);
    // resolved 防抖：告警连续 N 个周期消失才推送恢复
    private readonly Dictionary<string, int> _pendingResolveMisses = new(StringComparer.Ordinal);

    /// <summary>活跃告警 ID 集合（含已推送的），用于去重判定。测试可读取。</summary>
    internal IReadOnlySet<string> ActiveAlertIds => _activeAlertIds;

    public AlertWebhookNotifier(
        Func<IReadOnlyList<AlertRecord>> checkAlerts,
        HttpClient httpClient,
        IOptionsMonitor<RouterOptions> options,
        ILogger<AlertWebhookNotifier>? logger = null)
    {
        _checkAlerts = checkAlerts ?? throw new ArgumentNullException(nameof(checkAlerts));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AlertWebhookNotifier>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var routing = _options.CurrentValue.Routing;
        if (string.IsNullOrWhiteSpace(routing.AlertWebhookUrl))
        {
            _logger.LogInformation("Alert webhook not configured; notification service disabled.");
            return;
        }

        int intervalSeconds = Math.Max(5, routing.AlertWebhookIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
                await DrainPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert webhook check failed; will retry next cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 对比最新告警快照与活跃集合，产生推送事件（新增 alert / 消失 resolved）。
    /// </summary>
    internal void Tick()
    {
        var current = _checkAlerts();
        var currentIds = new HashSet<string>(current.Select(a => a.Id), StringComparer.Ordinal);

        // 新出现的告警（含启动时已存在的存量告警——首次周期即推送当前状态）。
        foreach (var alert in current)
        {
            if (!_activeAlertIds.Contains(alert.Id))
            {
                _pending.Enqueue((alert, IsResolved: false));
                _lastMessages[alert.Id] = alert.Message;
            }
        }

        // 已消失（恢复）的告警：连续 2 个周期不存在才推送 resolved（防 flapping 告警恢复风暴）。
        // 未满周期的先保留在活跃集中，否则下一周期无从复查、resolved 永远不会推送。
        var nextActive = new HashSet<string>(currentIds, StringComparer.Ordinal);
        foreach (var id in _activeAlertIds)
        {
            if (currentIds.Contains(id))
            {
                _pendingResolveMisses.Remove(id);
                continue;
            }

            int misses = _pendingResolveMisses.TryGetValue(id, out var m) ? m + 1 : 1;
            if (misses < 2)
            {
                _pendingResolveMisses[id] = misses;
                nextActive.Add(id);
                continue;
            }

            _pendingResolveMisses.Remove(id);
            string detail = _lastMessages.TryGetValue(id, out var lastMessage)
                ? lastMessage
                : $"Alert '{id}' recovered.";
            _pending.Enqueue((
                new AlertRecord(id, "info", "recovery", $"Recovered: {detail}", DateTime.UtcNow),
                IsResolved: true));
            _lastMessages.Remove(id);
        }

        _activeAlertIds = nextActive;
    }

    /// <summary>
    /// 逐条推送挂起事件；失败时保留队首等待下周期重试。
    /// </summary>
    internal async Task DrainPendingAsync(CancellationToken ct)
    {
        string? webhookUrl = _options.CurrentValue.Routing.AlertWebhookUrl;
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        // 仅允许 http(s)，拒绝其他 scheme 的错误配置
        if (!webhookUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("AlertWebhookUrl must start with http:// or https://; push skipped");
            return;
        }

        while (_pending.Count > 0)
        {
            var (alert, isResolved) = _pending.Peek();
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(webhookUrl, new
                {
                    eventType = isResolved ? "resolved" : "alert",
                    timestamp = alert.Timestamp,
                    alert
                }, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                _pending.Dequeue();
                _logger.LogInformation("Alert webhook pushed: {Event} {Id} ({Level})", isResolved ? "resolved" : "alert", alert.Id, alert.Level);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Alert webhook push failed for {Id}; will retry next cycle", alert.Id);
                break;
            }
        }
    }
}
