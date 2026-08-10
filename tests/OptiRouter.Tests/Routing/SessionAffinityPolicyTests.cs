using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
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
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

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
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

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
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", new AffinityRecord("strong-a", DateTimeOffset.UtcNow));

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
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

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
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess1", new AffinityRecord("medium-a", DateTimeOffset.UtcNow));

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

/// <summary>可推进的假时钟，用于验证粘性时间戳的新鲜度判断。</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>返回固定 RouterOptions 的 IOptionsMonitor stub。</summary>
public sealed class StubOptionsMonitor : IOptionsMonitor<RouterOptions>
{
    private readonly RouterOptions _value;
    public StubOptionsMonitor(RouterOptions value) => _value = value;
    public RouterOptions CurrentValue => _value;
    public RouterOptions Get(string? name) => _value;
    public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
}

public sealed class RecordAffinityTests
{
    private static OutcomeRecorder MakeRecorder(IMemoryCache cache, MutableTimeProvider clock, RouterOptions opts)
    {
        // RecordAffinity 只触碰 _options/_affinityCache/_timeProvider，其余依赖传 null?。
        return new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: new StubOptionsMonitor(opts),
            affinityCache: cache,
            tsStore: null!,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: null!,
            timeProvider: clock);
    }

    private static RouterOptions AffinityOpts(int ttlSeconds = 600) => new()
    {
        Routing = { EnableSessionAffinity = true, SessionAffinityTtlSeconds = ttlSeconds }
    };

    [Fact]
    public void WeakSignal_DoesNotOverrideFreshStrong()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTimeProvider();
        var recorder = MakeRecorder(cache, clock, AffinityOpts());

        recorder.RecordAffinity("sess1", "main-model", AffinitySignal.Strong);
        // 旁路（Cascade/Fusion/Race）紧接着写弱信号，不应覆盖主链新鲜偏好。
        recorder.RecordAffinity("sess1", "side-model", AffinitySignal.Weak);

        var stored = cache.Get<AffinityRecord>(SessionAffinityPolicy.CacheKeyPrefix + "sess1");
        Assert.NotNull(stored);
        Assert.Equal("main-model", stored!.ModelName);
    }

    [Fact]
    public void WeakSignal_TakesOverWhenStrongIsStale()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTimeProvider();
        var recorder = MakeRecorder(cache, clock, AffinityOpts());

        recorder.RecordAffinity("sess1", "main-model", AffinitySignal.Strong);
        // 时间推进超过一个 TTL 周期 → 主链粘性视为不新鲜，弱信号可接管。
        clock.Now = clock.Now.AddSeconds(600);
        recorder.RecordAffinity("sess1", "side-model", AffinitySignal.Weak);

        var stored = cache.Get<AffinityRecord>(SessionAffinityPolicy.CacheKeyPrefix + "sess1");
        Assert.NotNull(stored);
        Assert.Equal("side-model", stored!.ModelName);
    }

    [Fact]
    public void WeakSignal_WritesWhenNoExistingAffinity()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTimeProvider();
        var recorder = MakeRecorder(cache, clock, AffinityOpts());

        recorder.RecordAffinity("sess1", "side-model", AffinitySignal.Weak);

        var stored = cache.Get<AffinityRecord>(SessionAffinityPolicy.CacheKeyPrefix + "sess1");
        Assert.NotNull(stored);
        Assert.Equal("side-model", stored!.ModelName);
    }

    [Fact]
    public void StrongSignal_AlwaysOverridesWeak()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTimeProvider();
        var recorder = MakeRecorder(cache, clock, AffinityOpts());

        recorder.RecordAffinity("sess1", "side-model", AffinitySignal.Weak);
        recorder.RecordAffinity("sess1", "main-model", AffinitySignal.Strong);

        var stored = cache.Get<AffinityRecord>(SessionAffinityPolicy.CacheKeyPrefix + "sess1");
        Assert.NotNull(stored);
        Assert.Equal("main-model", stored!.ModelName);
    }
}
