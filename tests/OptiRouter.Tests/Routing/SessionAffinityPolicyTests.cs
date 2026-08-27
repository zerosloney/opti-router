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

    // ===== SessionLatencyTracker + 延迟熔断逃生通道 =====

    [Fact]
    public void LatencyTracker_RecordsAndComputesAverage()
    {
        var tracker = new SessionLatencyTracker();
        tracker.Record("sess1", 1000);
        tracker.Record("sess1", 2000);
        tracker.Record("sess1", 3000);
        Assert.True(tracker.TryGetRecentAverage("sess1", out double avg));
        Assert.Equal(2000.0, avg, 0);
    }

    [Fact]
    public void LatencyTracker_CleansUpExpiredEntriesOnLaterRecord_AndRetainsActive()
    {
        var clock = new MutableTimeProvider();
        var tracker = new SessionLatencyTracker(clock);

        tracker.Record("sess-expired", 1000, 16, TimeSpan.FromMinutes(1));
        tracker.Record("sess-active", 2000, 16, TimeSpan.FromMinutes(10));
        Assert.Equal(2, tracker.EntryCount);

        clock.Now = clock.Now.AddMinutes(2);
        tracker.Record("sess-trigger", 3000, 16, TimeSpan.FromMinutes(10));

        Assert.Equal(2, tracker.EntryCount);
        Assert.False(tracker.TryGetRecentAverage("sess-expired", out _));
        Assert.True(tracker.TryGetRecentAverage("sess-active", out double activeAverage));
        Assert.Equal(2000.0, activeAverage, 0);
    }

    [Fact]
    public void LatencyTracker_IgnoresInvalidInput()
    {
        var tracker = new SessionLatencyTracker();
        tracker.Record(null, 1000);
        tracker.Record("", 1000);
        tracker.Record("sess1", 0);
        tracker.Record("sess1", -5);
        Assert.False(tracker.TryGetRecentAverage("sess1", out _));
        Assert.False(tracker.TryGetRecentAverage("nonexistent", out _));
    }

    [Fact]
    public void Affinity_LatencyEscape_AboveThreshold_SkipsStickyModel()
    {
        // 5 次平均 50s > 30s 阈值 → 跳过粘性，走主链
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        for (int i = 0; i < 5; i++) tracker.Record("sess-slow", 50_000);
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess-slow", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600,
                SessionAffinityEscapeAvgLatencyMs = 30_000,
                SessionAffinityEscapeWindowSize = 5
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };
        var result = policy.Apply(Context(opts, "sess-slow"), previous);

        // 没有提升 medium-b
        Assert.NotEqual("medium-b", result.Candidates[0].Name);
        Assert.Contains("escape: avg-latency", result.Reason);
    }

    [Fact]
    public void Affinity_LatencyEscape_BelowThreshold_PromotesAsNormal()
    {
        // 5 次平均 5s < 30s 阈值 → 正常提升粘性
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        for (int i = 0; i < 5; i++) tracker.Record("sess-fast", 5_000);
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess-fast", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600,
                SessionAffinityEscapeAvgLatencyMs = 30_000,
                SessionAffinityEscapeWindowSize = 5
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };
        var result = policy.Apply(Context(opts, "sess-fast"), previous);

        Assert.Equal("medium-b", result.Candidates[0].Name);
        Assert.Contains("promoted 'medium-b' to primary", result.Reason);
    }

    [Fact]
    public void Affinity_LatencyEscape_UsesRecentWindowAfterLatencyRecovers()
    {
        // 最近 5 次慢请求触发逃生；随后 5 次较快请求应让最近窗口恢复到阈值以下。
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        for (int i = 0; i < 5; i++) tracker.Record("sess-recovered", 50_000);
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess-recovered",
            new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600,
                SessionAffinityEscapeAvgLatencyMs = 30_000,
                SessionAffinityEscapeWindowSize = 5
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };

        var escaped = policy.Apply(Context(opts, "sess-recovered"), previous);
        Assert.NotEqual("medium-b", escaped.Candidates[0].Name);
        Assert.Contains("escape: avg-latency", escaped.Reason);

        for (int i = 0; i < 5; i++) tracker.Record("sess-recovered", 20_000);

        var recovered = policy.Apply(Context(opts, "sess-recovered"), previous);
        Assert.Equal("medium-b", recovered.Candidates[0].Name);
        Assert.Contains("promoted 'medium-b' to primary", recovered.Reason);
    }

    [Fact]
    public void Affinity_LatencyEscape_WindowLargerThanSixteenCanTrigger()
    {
        const int windowSize = 20;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        for (int i = 0; i < windowSize; i++) tracker.Record("sess-large-window", 50_000, windowSize, TimeSpan.FromMinutes(10));
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess-large-window",
            new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600,
                SessionAffinityEscapeAvgLatencyMs = 30_000,
                SessionAffinityEscapeWindowSize = windowSize
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };

        var result = policy.Apply(Context(opts, "sess-large-window"), previous);

        Assert.NotEqual("medium-b", result.Candidates[0].Name);
        Assert.Contains("escape: avg-latency", result.Reason);
    }

    [Fact]
    public void Affinity_LatencyEscape_DisabledByDefault_BehavesAsBefore()
    {
        // EscapeAvgLatencyMs=0 (默认关闭) → tracker 即使有数据也不影响
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        for (int i = 0; i < 5; i++) tracker.Record("sess", 100_000);
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600
                // EscapeAvgLatencyMs 默认 0
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };
        var result = policy.Apply(Context(opts, "sess"), previous);

        Assert.Equal("medium-b", result.Candidates[0].Name);
        Assert.DoesNotContain("escape", result.Reason);
    }

    [Fact]
    public void Affinity_LatencyEscape_InsufficientSamples_DoesNotTrigger()
    {
        // 样本不足（< windowSize=5）→ 放行粘性，避免误伤首次访问的 session
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new SessionLatencyTracker();
        // 只写 2 次（window=5）
        tracker.Record("sess-new", 50_000);
        tracker.Record("sess-new", 60_000);
        cache.Set(SessionAffinityPolicy.CacheKeyPrefix + "sess-new", new AffinityRecord("medium-b", DateTimeOffset.UtcNow));

        var opts = new RouterOptions
        {
            Routing = {
                EnableSessionAffinity = true,
                SessionAffinityTtlSeconds = 600,
                SessionAffinityEscapeAvgLatencyMs = 30_000,
                SessionAffinityEscapeWindowSize = 5
            }
        };
        var policy = new SessionAffinityPolicy(cache, tracker);
        var previous = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions> { GetModels()[0], GetModels()[1], GetModels()[2] },
            Reason = "init"
        };
        var result = policy.Apply(Context(opts, "sess-new"), previous);

        // 样本不足仍走粘性
        Assert.Equal("medium-b", result.Candidates[0].Name);
        Assert.Contains("promoted", result.Reason);
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
    private static OutcomeRecorder MakeRecorder(
        IMemoryCache cache,
        MutableTimeProvider clock,
        RouterOptions opts,
        SessionLatencyTracker? sessionLatencyTracker = null)
    {
        // RecordAffinity 只触碰与测试相关的依赖，其余依赖传 null。
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
            timeProvider: clock,
            sessionLatencyTracker: sessionLatencyTracker);
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
    public void WeakSignal_DoesNotOverrideFreshStrong_ButRecordsLatency()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new MutableTimeProvider();
        var tracker = new SessionLatencyTracker(clock);
        var recorder = MakeRecorder(cache, clock, AffinityOpts(), tracker);

        recorder.RecordAffinity("sess1", "main-model", AffinitySignal.Strong);
        recorder.RecordAffinity("sess1", "side-model", AffinitySignal.Weak, latencyMs: 12_345);

        var stored = cache.Get<AffinityRecord>(SessionAffinityPolicy.CacheKeyPrefix + "sess1");
        Assert.NotNull(stored);
        Assert.Equal("main-model", stored!.ModelName);
        Assert.True(tracker.TryGetRecentAverage("sess1", out double average));
        Assert.Equal(12_345d, average, 0);
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
