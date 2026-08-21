using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Health;
using OptiRouter.Routing;
using OptiRouter.Tests.Endpoints;

namespace OptiRouter.Tests.Health;

public sealed class ModelHealthProbeServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task ProbeFailure_Only5xxUpdatesHealth(
        HttpStatusCode statusCode,
        bool expectHealthFailure)
    {
        var endpoint = new ModelEndpointOptions { Name = "model-a", Enabled = true };
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        var result = new ModelHealthResult(
            Healthy: false,
            LatencyMs: 10,
            Error: "safe-error",
            StatusCode: statusCode,
            Metadata: statusCode == HttpStatusCode.TooManyRequests
                ? new UpstreamResponseMetadata { RequestsRemaining = 0, RequestsResetAt = reset }
                : null);
        var options = new RouterOptions();
        options.Models.Add(endpoint);
        options.Routing.FailoverFailureThreshold = 1;
        var health = new ModelHealthTracker();
        var quota = new UpstreamQuotaStateStore();
        var service = new ModelHealthProbeService(
            new ProbeProvider(new ProbeClient(endpoint, result)),
            health,
            quota,
            new FakeRouterOptionsMonitor(options),
            NullLogger<ModelHealthProbeService>.Instance);

        MethodInfo method = typeof(ModelHealthProbeService).GetMethod(
            "ProbeAllAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        Assert.Equal(expectHealthFailure ? CircuitState.Open : CircuitState.Closed,
            health.GetState(endpoint.Name));
        if (!expectHealthFailure)
            Assert.True(quota.GetSnapshot(endpoint.Name)!.IsExhausted(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ProbeAll_SkipsModelsWithRecentSuccess()
    {
        // 门控回归：近期有真实流量成功的模型不再被主动探活——
        // 实测探活（固定 5s 超时 / 密钥轮换期 401）曾把真实请求正常的模型反复熔断。
        var endpoint = new ModelEndpointOptions { Name = "model-a", Enabled = true };
        var countingClient = new CountingProbeClient(endpoint);
        var options = new RouterOptions();
        options.Models.Add(endpoint);
        var health = new ModelHealthTracker();
        health.RecordSuccess("model-a"); // 真实流量刚成功

        var service = new ModelHealthProbeService(
            new ProbeProvider(countingClient),
            health,
            new UpstreamQuotaStateStore(),
            new FakeRouterOptionsMonitor(options),
            NullLogger<ModelHealthProbeService>.Instance);

        MethodInfo method = typeof(ModelHealthProbeService).GetMethod(
            "ProbeAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(service, [CancellationToken.None])!;

        Assert.Equal(0, countingClient.ProbeCalls);

        // 窗口过期后恢复探活：新 tracker 无近期成功记录 → 探活执行
        var health2 = new ModelHealthTracker();
        var service2 = new ModelHealthProbeService(
            new ProbeProvider(countingClient),
            health2,
            new UpstreamQuotaStateStore(),
            new FakeRouterOptionsMonitor(options),
            NullLogger<ModelHealthProbeService>.Instance);
        await (Task)method.Invoke(service2, [CancellationToken.None])!;
        Assert.Equal(1, countingClient.ProbeCalls);
    }

    [Fact]
    public async Task ProbeAll_ExternalCancellation_DoesNotTripCircuit()
    {
        // 关停噪声回归：外部取消（进程关停）曾被计为探活失败熔断健康模型，
        // 并连锁触发 EventLog 关停竞态刷屏。取消不是健康信号。
        var endpoint = new ModelEndpointOptions { Name = "model-a", Enabled = true };
        var cancellingClient = new CancellingProbeClient(endpoint);
        var options = new RouterOptions();
        options.Models.Add(endpoint);
        options.Routing.FailoverFailureThreshold = 1;
        var health = new ModelHealthTracker();

        var service = new ModelHealthProbeService(
            new ProbeProvider(cancellingClient),
            health,
            new UpstreamQuotaStateStore(),
            new FakeRouterOptionsMonitor(options),
            NullLogger<ModelHealthProbeService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 模拟外部取消（关停）
        MethodInfo method = typeof(ModelHealthProbeService).GetMethod(
            "ProbeAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // 已取消的 token 传给私有方法：取消异常必须被识别为关停信号
        await (Task)method.Invoke(service, [cts.Token])!;

        Assert.Equal(CircuitState.Closed, health.GetState(endpoint.Name));
    }

    private sealed class CancellingProbeClient(ModelEndpointOptions endpoint) : IModelClient
    {
        public ModelEndpointOptions Endpoint => endpoint;
        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
            => Task.FromCanceled<ModelHealthResult>(cancellationToken);
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ProbeProvider(IModelClient client) : IModelClientProvider
    {
        public IModelClient GetClient(ModelEndpointOptions endpoint) => client;
    }

    private sealed class CountingProbeClient(ModelEndpointOptions endpoint) : IModelClient
    {
        public int ProbeCalls;
        public ModelEndpointOptions Endpoint => endpoint;
        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        {
            ProbeCalls++;
            return Task.FromResult(new ModelHealthResult(true, 1));
        }
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ProbeClient(
        ModelEndpointOptions endpoint,
        ModelHealthResult result) : IModelClient
    {
        public ModelEndpointOptions Endpoint => endpoint;
        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
            => Task.FromResult(result);
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
