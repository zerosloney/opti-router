using OptiRouter.Mesh;
using Xunit;

namespace OptiRouter.Tests.Mesh;

public class DistributedStateMeshTests
{
    [Fact]
    public async Task InMemoryMesh_PublishAndSubscribe_DeliversPayload()
    {
        var mesh = new InMemoryDistributedStateMesh("node-1");
        string? receivedChannel = null;
        KvCachePrefixSyncEvent? receivedPayload = null;

        using var sub = mesh.Subscribe<KvCachePrefixSyncEvent>("channel.test", payload =>
        {
            receivedChannel = "channel.test";
            receivedPayload = payload;
        });

        var testEvent = new KvCachePrefixSyncEvent(
            SenderNodeId: "node-2",
            Tokens: new[] { "system", "prompt", "chunk" },
            ModelName: "gpt-4o",
            Timestamp: DateTimeOffset.UtcNow);

        await mesh.PublishAsync("channel.test", testEvent);

        Assert.Equal("channel.test", receivedChannel);
        Assert.NotNull(receivedPayload);
        Assert.Equal("node-2", receivedPayload.SenderNodeId);
        Assert.Equal("gpt-4o", receivedPayload.ModelName);
        Assert.Equal(3, receivedPayload.Tokens.Count);

        var stats = mesh.GetStats();
        Assert.Equal(1, stats.PublishedEventsCount);
        Assert.Equal(1, stats.ReceivedEventsCount);
        Assert.Equal(1, stats.ActiveSubscriptionsCount);
    }

    [Fact]
    public async Task InMemoryMesh_Unsubscribe_StopsReceivingEvents()
    {
        var mesh = new InMemoryDistributedStateMesh("node-1");
        int count = 0;

        var sub = mesh.Subscribe<CostLedgerSyncEvent>("channel.cost", _ => count++);

        await mesh.PublishAsync("channel.cost", new CostLedgerSyncEvent("node-2", 0.05m, "session-1", DateTimeOffset.UtcNow));
        Assert.Equal(1, count);

        sub.Dispose();

        await mesh.PublishAsync("channel.cost", new CostLedgerSyncEvent("node-2", 0.05m, "session-1", DateTimeOffset.UtcNow));
        Assert.Equal(1, count); // Not called after dispose

        var stats = mesh.GetStats();
        Assert.Equal(0, stats.ActiveSubscriptionsCount);
    }

    [Fact]
    public async Task InMemoryMesh_SubscriberException_DoesNotBreakOtherSubscribers()
    {
        var mesh = new InMemoryDistributedStateMesh("node-1");
        bool secondCalled = false;

        mesh.Subscribe<KalmanLatencySyncEvent>("channel.kalman", _ => throw new InvalidOperationException("boom"));
        mesh.Subscribe<KalmanLatencySyncEvent>("channel.kalman", _ => secondCalled = true);

        await mesh.PublishAsync("channel.kalman", new KalmanLatencySyncEvent("node-2", "claude-3-5", 350.0, DateTimeOffset.UtcNow));

        Assert.True(secondCalled);
    }
}
