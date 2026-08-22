using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using OptiRouter.Tests.Endpoints;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 会话粘性回载：重启后 IMemoryCache 粘性清空，活跃 harness 会话首个请求重新抽签
/// 换上游会烧掉整个 prompt 缓存前缀（实测 116K 全量 miss）。回载从审计恢复
/// "每会话最近成功模型"，粘性即刻接续。
/// </summary>
public class SessionAffinityWarmupTests
{
    private static RequestAuditRecord Rec(
        string model, string? sessionId, bool success = true, DateTime? timestamp = null)
        => new(
            Timestamp: timestamp ?? DateTime.UtcNow,
            RequestId: "req-1",
            Model: model,
            EstimatedInputTokens: 100,
            PromptTokens: 100,
            CompletionTokens: 10,
            Cost: 0.001m,
            LatencyMs: 50,
            SessionId: sessionId,
            RoutingReason: "test",
            Success: success,
            ErrorMessage: null,
            IsStreaming: true);

    private static (SessionAffinityWarmupService Svc, IMemoryCache Cache) Create(
        InMemoryRequestAuditStore audit, RouterOptions? options = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
        var svc = new SessionAffinityWarmupService(
            audit, cache, new FakeRouterOptionsMonitor(options ?? new RouterOptions()),
            NullLogger<SessionAffinityWarmupService>.Instance);
        return (svc, cache);
    }

    [Fact]
    public void RestoreFromAudit_PinsEachSessionToLatestSuccessfulModel()
    {
        var audit = new InMemoryRequestAuditStore(capacity: 64);
        // 会话 A：先成功 model-1，后成功 model-2 → 应粘 model-2（最新）
        audit.Append(Rec("model-1", "session-a", timestamp: DateTime.UtcNow.AddMinutes(-10)));
        audit.Append(Rec("model-2", "session-a", timestamp: DateTime.UtcNow.AddMinutes(-5)));
        // 会话 B：仅失败记录 → 不粘
        audit.Append(Rec("model-x", "session-b", success: false));
        // 无会话 → 跳过
        audit.Append(Rec("model-3", null));

        var (svc, cache) = Create(audit, new RouterOptions { Routing = { EnableSessionAffinity = true } });

        int restored = svc.RestoreFromAudit();

        Assert.Equal(1, restored);
        Assert.True(cache.TryGetValue<AffinityRecord>(
            SessionAffinityPolicy.CacheKeyPrefix + "session-a", out var record));
        Assert.NotNull(record);
        Assert.Equal("model-2", record.ModelName);
        Assert.False(cache.TryGetValue(SessionAffinityPolicy.CacheKeyPrefix + "session-b", out _));
    }

    [Fact]
    public void RestoreFromAudit_DisabledAffinity_ReturnsZero()
    {
        var (svc, _) = Create(new InMemoryRequestAuditStore(),
            new RouterOptions { Routing = { EnableSessionAffinity = false } });

        Assert.Equal(0, svc.RestoreFromAudit());
    }
}
