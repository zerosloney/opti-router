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

    private sealed class ProbeProvider(IModelClient client) : IModelClientProvider
    {
        public IModelClient GetClient(ModelEndpointOptions endpoint) => client;
    }

    private sealed class ProbeClient(
        ModelEndpointOptions endpoint,
        ModelHealthResult result) : IModelClient
    {
        public ModelEndpointOptions Endpoint => endpoint;
        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
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
