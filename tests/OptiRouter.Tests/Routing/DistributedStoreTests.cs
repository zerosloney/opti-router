using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class DistributedStoreTests
{
    [Fact]
    public void RedisCostLedgerStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new RedisCostLedgerStore(connectionString: null);

        decimal total1 = store.AddTotal(1.5m);
        Assert.Equal(1.5m, total1);

        decimal fetchedTotal = store.GetTotal();
        Assert.Equal(1.5m, fetchedTotal);

        decimal daily = store.AddDaily(DateTime.UtcNow, 2.0m);
        Assert.Equal(2.0m, daily);

        store.SaveCircuitState("gpt-4o", CircuitState.Open, 5, DateTime.UtcNow.AddMinutes(5));
        var states = store.LoadCircuitStates();
        Assert.True(states.ContainsKey("gpt-4o"));
        Assert.Equal(CircuitState.Open, states["gpt-4o"].State);
    }

    [Fact]
    public void PostgresCostLedgerStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new PostgresCostLedgerStore(connectionString: null);

        decimal total = store.AddTotal(3.0m);
        Assert.Equal(3.0m, total);

        decimal fetched = store.GetTotal();
        Assert.Equal(3.0m, fetched);

        decimal session = store.AddSession("sess-100", 0.8m);
        Assert.Equal(0.8m, session);
    }

    [Fact]
    public void PostgresRequestAuditStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new PostgresRequestAuditStore(connectionString: null);

        var record = new RequestAuditRecord(
            Timestamp: DateTime.UtcNow,
            RequestId: "req-test-1",
            Model: "gpt-4o-mini",
            EstimatedInputTokens: 100,
            PromptTokens: 100,
            CompletionTokens: 50,
            Cost: 0.001m,
            LatencyMs: 120,
            SessionId: "sess-1",
            RoutingReason: "test",
            Success: true,
            ErrorMessage: null,
            IsStreaming: false
        );

        store.Append(record);

        var recent = store.GetRecent(10);
        Assert.Single(recent);
        Assert.Equal("req-test-1", recent[0].RequestId);
    }

    [Fact]
    public void RoutingOptions_OtlpTracingDefaults_AreCorrect()
    {
        var options = new RoutingOptions();

        Assert.False(options.EnableOtlpTracing);
        Assert.Equal("http://localhost:4317", options.OtlpEndpoint);
        Assert.Equal("grpc", options.OtlpProtocol);
        Assert.Equal("OptiRouter", options.OtlpServiceName);
    }
}
