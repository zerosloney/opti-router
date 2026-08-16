using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class KvCacheLocalityTests
{
    [Fact]
    public void KvCachePrefixTrie_RecordsAndMatchesWarmCache()
    {
        var trie = new KvCachePrefixTrie(TimeSpan.FromMinutes(5));

        var req1 = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "You are an expert C# compiler architect with deep knowledge of Roslyn and RyuJIT."),
                ChatMessage.FromText("user", "Explain how SIMD Vector<T> is inlined by RyuJIT.")
            }
        };

        trie.RecordCachePrefix(req1, "deepseek-coder");

        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deepseek-coder", "gpt-4o" };
        var match = trie.FindBestMatchingModel(req1, candidateNames);

        Assert.NotNull(match);
        Assert.Equal("deepseek-coder", match.ModelName);
        Assert.True(match.MatchedPrefixLength >= 3);
        Assert.True(match.SavingsRatio > 0.3);
    }

    [Fact]
    public void KvCacheLocalityPolicy_PromotesWarmCacheModel()
    {
        var trie = new KvCachePrefixTrie(TimeSpan.FromMinutes(5));

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "Standard System Prompt for Financial Audit Engine v2.0"),
                ChatMessage.FromText("user", "Analyze Q3 balance sheet.")
            }
        };

        // Record warm cache for gpt-4o
        trie.RecordCachePrefix(req, "gpt-4o");

        var policy = new KvCacheLocalityPolicy(trie);

        var modelA = new ModelEndpointOptions { Name = "claude-3-5-sonnet", Tier = ModelTier.Strong, MaxContextTokens = 128000 };
        var modelB = new ModelEndpointOptions { Name = "gpt-4o", Tier = ModelTier.Strong, MaxContextTokens = 128000 };

        var candidates = new List<ModelEndpointOptions> { modelA, modelB };

        var context = new RouterContext
        {
            Request = req,
            AllModels = candidates,
            Options = new RouterOptions
            {
                Routing = new RoutingOptions
                {
                    EnableKvCacheLocality = true
                }
            }
        };

        var decision = new RouterDecision { Candidates = candidates, Reason = "init" };
        var result = policy.Apply(context, decision);

        // gpt-4o should be promoted to index 0 because it has warm KV cache
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
        Assert.Contains("promoted warm kv-cache model 'gpt-4o'", result.Reason);
    }
}
