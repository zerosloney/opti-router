using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public sealed class KvCachePrefixTrieTests
{
    /// <summary>
    /// 可推进时间的 TimeProvider，用于验证 TTL 剪枝。
    /// </summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void RecordCachePrefix_ExceedingMaxNodes_PrunesExpiredSubtrees()
    {
        var clock = new MutableTimeProvider();
        var trie = new KvCachePrefixTrie(
            cacheTtl: TimeSpan.FromMinutes(10),
            timeProvider: clock,
            maxNodes: 150,
            pruneMinInterval: TimeSpan.Zero);

        // 第一批：100 条前缀（根 + 100 个 pA* + shared + suffix ≈ 103 节点），全部活跃
        for (int i = 0; i < 100; i++)
        {
            trie.RecordCachePrefix(new[] { $"pA{i:D3}", "shared", "suffix" }, "model-a", clock.Now);
        }

        // 第二批：+1 分钟插入新前缀（模拟持续活跃流量），节点数超上限但全部活跃（无过期节点可回收）
        clock.Now = clock.Now.AddMinutes(1);
        for (int i = 0; i < 100; i++)
        {
            trie.RecordCachePrefix(new[] { $"pB{i:D3}", "shared", "suffix" }, "model-a", clock.Now);
        }

        int beforePrune = trie.NodeCount;
        Assert.True(beforePrune > 150, $"Node count {beforePrune} should exceed maxNodes (active nodes are not prunable)");

        // 推进时间：第一批（T0）过期，第二批（T0+1min）处于 TTL 边界内；插入触发者触发剪枝
        clock.Now = clock.Now.AddMinutes(10);
        trie.RecordCachePrefix(new[] { "pC000", "shared", "suffix" }, "model-a", clock.Now);

        // 第一批 300 个节点（pA* + 专属 shared/suffix）被回收，第二批与触发者保留
        Assert.True(trie.NodeCount <= beforePrune - 250,
            $"Expected ~{beforePrune - 300} nodes after prune, actual: {trie.NodeCount}");
        Assert.True(trie.NodeCount >= 300,
            $"Newly active prefixes must survive prune, actual: {trie.NodeCount}");
    }

    [Fact]
    public void FindBestMatchingModel_StillMatchesActivePrefixesAfterPrune()
    {
        var clock = new MutableTimeProvider();
        var trie = new KvCachePrefixTrie(
            cacheTtl: TimeSpan.FromMinutes(10),
            timeProvider: clock,
            maxNodes: 50,
            pruneMinInterval: TimeSpan.Zero);

        // 建立一批活跃前缀
        for (int i = 0; i < 100; i++)
        {
            trie.RecordCachePrefix(new[] { $"pB{i:D3}", "shared", "suffix" }, "model-a", clock.Now);
        }

        // 推进时间并插入触发者：只有 pB* 仍活跃（同一批次写入时间相同，全部存活）
        clock.Now = clock.Now.AddMinutes(2);
        trie.RecordCachePrefix(new[] { "pC000", "shared", "suffix" }, "model-a", clock.Now);

        // pB 前缀仍应命中 model-a
        var request = new OptiRouter.Clients.ChatRequest
        {
            Messages = new System.Collections.Generic.List<OptiRouter.Clients.ChatMessage>
            {
                OptiRouter.Clients.ChatMessage.FromText("user", "pB042 shared suffix")
            }
        };
        var hit = trie.FindBestMatchingModel(request, new HashSet<string> { "model-a" });

        Assert.NotNull(hit);
        Assert.Equal("model-a", hit.ModelName);
        Assert.True(hit.MatchedPrefixLength >= 3);
    }

    [Fact]
    public void FindBestMatchingModel_ExpiredPrefix_NoHit()
    {
        var clock = new MutableTimeProvider();
        var trie = new KvCachePrefixTrie(
            cacheTtl: TimeSpan.FromMinutes(10),
            timeProvider: clock);

        trie.RecordCachePrefix(new[] { "pX001", "shared", "suffix" }, "model-a", clock.Now);

        // 超过 TTL：即使节点仍在（未触发剪枝），命中判定也必须失败
        clock.Now = clock.Now.AddMinutes(15);

        var request = new OptiRouter.Clients.ChatRequest
        {
            Messages = new System.Collections.Generic.List<OptiRouter.Clients.ChatMessage>
            {
                OptiRouter.Clients.ChatMessage.FromText("user", "pX001 shared suffix")
            }
        };
        var hit = trie.FindBestMatchingModel(request, new HashSet<string> { "model-a" });

        Assert.Null(hit);
    }
}
