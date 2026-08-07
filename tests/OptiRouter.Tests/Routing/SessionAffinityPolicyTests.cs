using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class SessionAffinityPolicyTests
{
    private static List<ModelEndpointOptions> GetModels() => new()
    {
        new() { Name = "strong-a", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
        new() { Name = "medium-a", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
        new() { Name = "medium-b", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 32000 },
    };

    private static RouterContext Context(RouterOptions opts, string? sessionId, IReadOnlySet<string>? failed = null) => new()
    {
        Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") } },
        AllModels = GetModels(),
        Options = opts,
        SessionId = sessionId,
        FailedModels = failed ?? new HashSet<string>()
    };

    private static RouterOptions Opts(bool enabled = true) => new()
    {
        Routing = { EnableSessionAffinity = enabled, SessionAffinityTtlSeconds = 600 }
    };

    [Fact]
    public void Affinity_PromotesRememberedModelToFirst()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", "medium-b");

        var policy = new SessionAffinityPolicy(cache);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(), "sess1"), previous);

        Assert.Equal("medium-b", result.Candidates[0].Name);
        Assert.Contains("promoted 'medium-b' to primary", result.Reason);
    }

    [Fact]
    public void Affinity_NoSession_Passthrough()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var policy = new SessionAffinityPolicy(cache);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(), null), previous);

        Assert.Contains("no-session", result.Reason);
        Assert.Same(previous.Candidates, result.Candidates);
    }

    [Fact]
    public void Affinity_RememberedModelFailed_Skipped()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", "medium-b");

        var policy = new SessionAffinityPolicy(cache);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[1], GetModels()[2] },
            Reason = "init"
        };
        var failed = new HashSet<string> { "medium-b" };

        var result = policy.Apply(Context(Opts(), "sess1", failed), previous);

        // 不提升，候选顺序不变（medium-b 已失败本不应在候选，但断言不提升逻辑）
        Assert.Contains("remembered 'medium-b' failed, skipped", result.Reason);
    }

    [Fact]
    public void Affinity_RememberedModelNotInCandidates_Passthrough()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", "strong-a");

        var policy = new SessionAffinityPolicy(cache);
        // 候选链只有 medium 模型，strong-a 不在其中
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[1], GetModels()[2] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(), "sess1"), previous);

        Assert.Contains("not in candidates", result.Reason);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Affinity_Disabled_Passthrough()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", "medium-b");

        var policy = new SessionAffinityPolicy(cache);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(enabled: false), "sess1"), previous);

        Assert.Contains("disabled", result.Reason);
    }

    [Fact]
    public void Affinity_NoRecord_Passthrough()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var policy = new SessionAffinityPolicy(cache);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(), "sess-no-record"), previous);

        Assert.Contains("no-record", result.Reason);
    }

    [Fact]
    public void Affinity_AlreadyPrimary_NoReorder()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", "medium-a");

        var policy = new SessionAffinityPolicy(cache);
        var models = GetModels();
        // medium-a 已在首位
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { models[1], models[2] },
            Reason = "init"
        };

        var result = policy.Apply(Context(Opts(), "sess1"), previous);

        Assert.Contains("already primary", result.Reason);
        Assert.Equal("medium-a", result.Candidates[0].Name);
    }
}
