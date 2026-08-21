using OptiRouter.Clients;
using OptiRouter.Mesh;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Mesh;

public class DistributedMeshSynchronizerTests
{
    [Fact]
    public async Task Synchronizer_KvCachePrefix_SyncsAcrossNodes()
    {
        var sharedMesh = new InMemoryDistributedStateMesh("cluster-bus");

        // Node A setup
        var kvTrieA = new KvCachePrefixTrie();
        using var syncA = new DistributedMeshSynchronizer(new InMemoryDistributedStateMesh("node-a"), kvTrieA);

        // Connect both syncers via the shared bus
        var syncerMeshA = new InMemoryDistributedStateMesh("node-a");
        var syncerMeshB = new InMemoryDistributedStateMesh("node-b");

        // Use custom shared mesh simulation
        var kvTrieB = new KvCachePrefixTrie();
        using var sync1 = new DistributedMeshSynchronizer(sharedMesh, kvTrieA);
        using var sync2 = new DistributedMeshSynchronizer(sharedMesh, kvTrieB);

        var tokens = new List<string> { "System", "Prompt", "For", "Enterprise", "Knowledge", "Base" };

        // Node 1 broadcasts token prefix learned for "gpt-4o"
        var evt = new KvCachePrefixSyncEvent("node-1", tokens, "gpt-4o", DateTimeOffset.UtcNow);
        await sharedMesh.PublishAsync(DistributedMeshSynchronizer.ChannelKvCache, evt);

        // Check if Node B's Trie now has "gpt-4o" matching this prompt!
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "System Prompt For Enterprise Knowledge Base What is our quarterly goal?")
            }
        };

        var matchResult = kvTrieB.FindBestMatchingModel(request, new HashSet<string> { "gpt-4o" });
        Assert.NotNull(matchResult);
        Assert.Equal("gpt-4o", matchResult.ModelName);
        Assert.True(matchResult.MatchedPrefixLength >= 3);
    }

    [Fact]
    public async Task Synchronizer_KalmanLatency_SyncsAcrossNodes()
    {
        var sharedMesh = new InMemoryDistributedStateMesh("cluster-bus");

        var kalmanA = new KalmanLatencyTracker();
        var kalmanB = new KalmanLatencyTracker();

        using var sync1 = new DistributedMeshSynchronizer(sharedMesh, kalmanTracker: kalmanA);
        using var sync2 = new DistributedMeshSynchronizer(sharedMesh, kalmanTracker: kalmanB);

        // Node 1 sends latency observation of 1500ms
        var evt = new KalmanLatencySyncEvent("node-1", "gemini-pro", 1500.0, DateTimeOffset.UtcNow);
        await sharedMesh.PublishAsync(DistributedMeshSynchronizer.ChannelKalman, evt);

        var estB = kalmanB.GetEstimate("gemini-pro");
        Assert.True(estB.EstimatedLatencyMs > 500.0); // Kalman tracker moved towards 1500ms
    }

    [Fact]
    public async Task Synchronizer_CostLedger_SyncsAcrossNodes()
    {
        var sharedMesh = new InMemoryDistributedStateMesh("cluster-bus");

        var costLedgerA = new CostLedger();
        var costLedgerB = new CostLedger();

        using var sync1 = new DistributedMeshSynchronizer(sharedMesh, costLedger: costLedgerA);
        using var sync2 = new DistributedMeshSynchronizer(sharedMesh, costLedger: costLedgerB);

        // Node 1 spent $0.25 USD on session-42
        var evt = new CostLedgerSyncEvent("node-1", 0.25m, "session-42", DateTimeOffset.UtcNow);
        await sharedMesh.PublishAsync(DistributedMeshSynchronizer.ChannelCost, evt);

        Assert.Equal(0.25m, costLedgerB.GetDailySpend());
        Assert.Equal(0.25m, costLedgerB.GetSpend().Total);
        Assert.Equal(0.25m, costLedgerB.GetSessionSpend("session-42"));
    }

    [Fact]
    public async Task Synchronizer_PredictiveResilience_SyncsAcrossNodes()
    {
        var sharedMesh = new InMemoryDistributedStateMesh("cluster-bus");

        var resilienceA = new PredictiveResilienceEngine();
        var resilienceB = new PredictiveResilienceEngine();

        using var sync1 = new DistributedMeshSynchronizer(sharedMesh, resilienceEngine: resilienceA);
        using var sync2 = new DistributedMeshSynchronizer(sharedMesh, resilienceEngine: resilienceB);

        // Node 1 reports 6 consecutive failures on deepseek-r1
        //（时序桶由接收侧从 Timestamp.Minute 派生，事件不携带桶字段）
        for (int i = 0; i < 6; i++)
        {
            var evt = new PredictiveResilienceSyncEvent("node-1", "deepseek-r1", true, DateTimeOffset.UtcNow);
            await sharedMesh.PublishAsync(DistributedMeshSynchronizer.ChannelResilience, evt);
        }

        var risk = resilienceB.PredictCongestionRisk("deepseek-r1", lookaheadMinutes: 0);
        Assert.True(risk > 0.5);
    }
}
