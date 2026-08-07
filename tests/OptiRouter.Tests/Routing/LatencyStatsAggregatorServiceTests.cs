using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using Xunit;
using FakeOptionsMonitor = OptiRouter.Tests.Endpoints.FakeRouterOptionsMonitor;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// LatencyStatsAggregatorService 测试：验证后台聚合将审计记录统计刷新到 ILatencyStatsProvider。
/// </summary>
public class LatencyStatsAggregatorServiceTests
{
    private static RouterOptions EnabledOptions() => new()
    {
        Routing =
        {
            EnableLatencyAware = true,
            LatencyStatsWindowMinutes = 60,
            HealthProbeIntervalSeconds = 60
        }
    };

    private static RouterOptions DisabledOptions() => new()
    {
        Routing = { EnableLatencyAware = false }
    };

    [Fact]
    public async Task StartWithLatencyAware_PrewarmsAggregation_UpdatesCache()
    {
        // 塞审计记录（含成功+失败），启动服务触发预热轮聚合，验证 cache 被刷新。
        using var store = new InMemoryRequestAuditStore();
        store.Append(Sample("model-a", true, 100));
        store.Append(Sample("model-a", true, 200));
        store.Append(Sample("model-a", true, 300)); // 均值 200
        store.Append(Sample("model-a", false, 9999)); // 失败不计入
        store.Append(Sample("model-b", true, 50));

        var cache = new LatencyStatsCache();
        var options = new FakeOptionsMonitor(EnabledOptions());
        var logger = NullLogger<LatencyStatsAggregatorService>.Instance;
        using var service = new LatencyStatsAggregatorService(store, cache, options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // ExecuteAsync 启动时立即预热一轮（await AggregateAsync 在 while 前）。
        // 启动后立即取消，仅跑预热轮。
        await service.StartAsync(cts.Token);
        // 给预热轮一点时间完成（预热是同步 I/O 后更新 cache）。
        await Task.Delay(300, cts.Token);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        var a = cache.GetStats("model-a");
        var b = cache.GetStats("model-b");
        Assert.NotNull(a);
        Assert.Equal(200.0, a!.AverageLatencyMs, precision: 1);
        Assert.Equal(3, a.SampleCount); // 失败不计入
        Assert.NotNull(b);
        Assert.Equal(50.0, b!.AverageLatencyMs);
        Assert.Equal(1, b.SampleCount);
    }

    [Fact]
    public async Task StartWithLatencyAwareDisabled_DoesNotAggregate()
    {
        // EnableLatencyAware=false 时 ExecuteAsync 立即返回，cache 保持空。
        using var store = new InMemoryRequestAuditStore();
        store.Append(Sample("model-a", true, 100));

        var cache = new LatencyStatsCache();
        var options = new FakeOptionsMonitor(DisabledOptions());
        var logger = NullLogger<LatencyStatsAggregatorService>.Instance;
        using var service = new LatencyStatsAggregatorService(store, cache, options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await service.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // 关闭状态不聚合，cache 仍空。
        Assert.Null(cache.GetStats("model-a"));

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    private static RequestAuditRecord Sample(string model, bool success, long latencyMs) => new(
        Timestamp: DateTime.UtcNow,
        RequestId: "r-" + Guid.NewGuid().ToString("N")[..8],
        Model: model,
        EstimatedInputTokens: 100,
        PromptTokens: 80,
        CompletionTokens: 40,
        Cost: 0.001m,
        LatencyMs: latencyMs,
        SessionId: null,
        RoutingReason: "test",
        Success: success,
        ErrorMessage: success ? null : "err",
        IsStreaming: false);
}
