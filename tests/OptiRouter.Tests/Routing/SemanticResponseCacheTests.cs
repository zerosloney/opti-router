using OptiRouter.Clients;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class SemanticResponseCacheTests
{
    [Fact]
    public async Task SemanticResponseCache_HighSimilarityPrompt_HitsCache()
    {
        var cache = new SemanticResponseCache(maxEntries: 100);
        string originalPrompt = "请解释什么是面向对象编程中的多态性";
        string similarPrompt = "请说明面向对象编程里的多态性是什么";

        var dummyResponse = new RawChatResponse(
            Body: "{\"choices\":[{\"message\":{\"content\":\"多态是指同一个行为具有多个不同表现形式...\"}}]}",
            Usage: null,
            Metadata: null);

        await cache.StoreAsync(originalPrompt, dummyResponse, TimeSpan.FromMinutes(30));

        var (hit, response, similarity, matchedPrompt) = await cache.TryGetAsync(similarPrompt, similarityThreshold: 0.70f);

        Assert.True(hit);
        Assert.NotNull(response);
        Assert.Equal(dummyResponse.Body, response.Body);
        Assert.True(similarity >= 0.70);
        Assert.Equal(originalPrompt, matchedPrompt);
    }

    [Fact]
    public async Task SemanticResponseCache_LowSimilarityPrompt_MissesCache()
    {
        var cache = new SemanticResponseCache(maxEntries: 100);
        string prompt1 = "今天北京天气怎么样？";
        string prompt2 = "用 C# 编写一个快速排序算法。";

        var dummyResponse = new RawChatResponse(
            Body: "{\"choices\":[{\"message\":{\"content\":\"北京今天晴朗...\"}}]}",
            Usage: null,
            Metadata: null);

        await cache.StoreAsync(prompt1, dummyResponse, TimeSpan.FromMinutes(30));

        var (hit, response, similarity, matchedPrompt) = await cache.TryGetAsync(prompt2, similarityThreshold: 0.85f);

        Assert.False(hit);
        Assert.Null(response);
        Assert.True(similarity < 0.85);
    }

    [Fact]
    public async Task SemanticResponseCache_ExpiredItem_MissesCache()
    {
        var cache = new SemanticResponseCache(maxEntries: 100);
        string prompt = "这会很快过期";

        var dummyResponse = new RawChatResponse(
            Body: "{\"choices\":[{\"message\":{\"content\":\"过期测试\"}}]}",
            Usage: null,
            Metadata: null);

        await cache.StoreAsync(prompt, dummyResponse, TimeSpan.FromMilliseconds(10));
        await Task.Delay(50);

        var (hit, response, _, _) = await cache.TryGetAsync(prompt, similarityThreshold: 0.80f);

        Assert.False(hit);
        Assert.Null(response);
    }

    [Fact]
    public async Task SemanticResponseCache_WithVectorEngine_HitsSimilarPrompt()
    {
        var vectorEngine = new DenseEmbeddingVectorEngine();
        var cache = new SemanticResponseCache(maxEntries: 100, vectorEngine: vectorEngine);

        string prompt1 = "How to sort an array in C#?";
        string prompt2 = "How to sort an array in C# efficiently?";

        var dummyResponse = new RawChatResponse(
            Body: "{\"choices\":[{\"message\":{\"content\":\"Use Array.Sort or LINQ OrderBy\"}}]}",
            Usage: null,
            Metadata: null);

        await cache.StoreAsync(prompt1, dummyResponse, TimeSpan.FromMinutes(10));

        var (hit, response, similarity, matchedPrompt) = await cache.TryGetAsync(prompt2, similarityThreshold: 0.60f);

        Assert.True(hit);
        Assert.NotNull(response);
        Assert.Equal(dummyResponse.Body, response.Body);
        Assert.True(similarity >= 0.60);
        Assert.Equal(prompt1, matchedPrompt);
    }

    [Fact]
    public async Task SemanticResponseCache_DifferentPartitions_DoNotMatch()
    {
        var cache = new SemanticResponseCache(maxEntries: 100);
        string prompt = "同一个提示词不应跨安全分区命中";
        var response = new RawChatResponse("{\"content\":\"partition-a\"}", null);

        await cache.StoreAsync(prompt, response, TimeSpan.FromMinutes(30), partitionKey: "partition-a");

        var (hit, cached, _, matchedPrompt) = await cache.TryGetAsync(
            prompt,
            similarityThreshold: 0.99f,
            partitionKey: "partition-b");

        Assert.False(hit);
        Assert.Null(cached);
        Assert.Null(matchedPrompt);
    }

    [Fact]
    public async Task SemanticResponseCache_SamePromptInDifferentPartitions_ReturnsOwnResponse()
    {
        var cache = new SemanticResponseCache(maxEntries: 100);
        string prompt = "同一个提示词在不同分区应保存独立响应";
        var responseA = new RawChatResponse("{\"content\":\"partition-a\"}", null);
        var responseB = new RawChatResponse("{\"content\":\"partition-b\"}", null);

        await cache.StoreAsync(prompt, responseA, TimeSpan.FromMinutes(30), partitionKey: "partition-a");
        await cache.StoreAsync(prompt, responseB, TimeSpan.FromMinutes(30), partitionKey: "partition-b");

        var (hitA, cachedA, _, matchedA) = await cache.TryGetAsync(
            prompt,
            similarityThreshold: 0.99f,
            partitionKey: "partition-a");
        var (hitB, cachedB, _, matchedB) = await cache.TryGetAsync(
            prompt,
            similarityThreshold: 0.99f,
            partitionKey: "partition-b");

        Assert.True(hitA);
        Assert.True(hitB);
        Assert.Equal(responseA.Body, cachedA?.Body);
        Assert.Equal(responseB.Body, cachedB?.Body);
        Assert.Equal(prompt, matchedA);
        Assert.Equal(prompt, matchedB);
    }
}
