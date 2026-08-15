using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 测试学习系统 reward 记录到审计的完整链路。
/// </summary>
public class RewardLoggingTests
{
    private static OutcomeRecorder CreateRecorder(RoutingOptions? routing = null, IRequestAuditStore? auditStore = null)
    {
        var options = new RouterOptions { Routing = routing ?? new RoutingOptions() };
        return new OutcomeRecorder(
            auditStore: auditStore ?? new InMemoryRequestAuditStore(),
            metrics: null!,
            ledger: new CostLedger(),
            options: new FakeRouterOptionsMonitor(options),
            affinityCache: new MemoryCache(new MemoryCacheOptions()),
            tsStore: new ThompsonStateStore(),
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance);
    }

    private sealed class FakeRouterOptionsMonitor(RouterOptions current) : IOptionsMonitor<RouterOptions>
    {
        public RouterOptions CurrentValue => current;
        public RouterOptions Get(string? name) => current;
        public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
    }

    [Fact]
    public void RecordThompsonOutcome_ReturnsReward_AfterCostWeighting()
    {
        // Arrange: CostAwareWeight=0 时返回原始延迟 reward，无成本加权干扰
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = 1000,
            CostAwareWeight = 0 // 禁用成本加权，简化断言
        };
        var recorder = CreateRecorder(routing);
        var decision = new RouterDecision
        {
            Candidates = [FakeModel("gpt-3.5-turbo", ModelTier.Cheap)],
            Reason = "test",
            EstimatedInputTokens = 1000,
            RequestMessageCount = 5,
            RequestIsStreaming = false
        };

        // Act: 延迟 500ms，目标 1000ms → reward 在 0.7~1.0 之间
        double reward = recorder.RecordThompsonOutcome("gpt-3.5-turbo", 500, decision);

        // Assert: 延迟 500ms 小于目标 1000ms，reward 应在 0.7~1.0 之间（无阶跃）
        Assert.InRange(reward, 0.7, 1.0);
    }

    [Fact]
    public void RecordThompsonRaceCancelled_ReturnsConfiguredReward()
    {
        // Arrange
        var routing = new RoutingOptions
        {
            ThompsonRaceCancelledReward = 0.5,
            CostAwareWeight = 0 // 禁用成本加权
        };
        var recorder = CreateRecorder(routing);
        var decision = new RouterDecision
        {
            Candidates = [FakeModel("gpt-3.5-turbo", ModelTier.Cheap)],
            Reason = "test",
            EstimatedInputTokens = 1000,
            RequestMessageCount = 5,
            RequestIsStreaming = false
        };

        // Act
        double reward = recorder.RecordThompsonRaceCancelled("gpt-3.5-turbo", decision);

        // Assert: 竞速取消 reward 应直接等于配置值（无成本加权时）
        Assert.Equal(0.5, reward);
    }

    [Fact]
    public void RecordQualityOutcome_ReturnsClampedReward()
    {
        // Arrange
        var routing = new RoutingOptions
        {
            CostAwareWeight = 0 // 禁用成本加权
        };
        var recorder = CreateRecorder(routing);
        var decision = new RouterDecision
        {
            Candidates = [FakeModel("gpt-3.5-turbo", ModelTier.Cheap)],
            Reason = "test",
            EstimatedInputTokens = 1000,
            RequestMessageCount = 5,
            RequestIsStreaming = false
        };

        // Act: 质量 reward 0.8 应 Clamp 到 [0,1] 后返回
        double reward = recorder.RecordQualityOutcome("gpt-3.5-turbo", 0.8, decision);

        // Assert
        Assert.Equal(0.8, reward);
    }

    [Fact]
    public void RecordQualityOutcome_ClampsNegativeAndLargeValues()
    {
        // Arrange
        var routing = new RoutingOptions
        {
            CostAwareWeight = 0
        };
        var recorder = CreateRecorder(routing);
        var decision = new RouterDecision
        {
            Candidates = [FakeModel("gpt-3.5-turbo", ModelTier.Cheap)],
            Reason = "test",
            EstimatedInputTokens = 1000,
            RequestMessageCount = 5,
            RequestIsStreaming = false
        };

        // Act & Assert
        Assert.Equal(0.0, recorder.RecordQualityOutcome("gpt-3.5-turbo", -1.0, decision));
        Assert.Equal(1.0, recorder.RecordQualityOutcome("gpt-3.5-turbo", 2.0, decision));
    }

    [Fact]
    public void RecordAudit_PassesRewardToRecord()
    {
        // Arrange
        var auditStore = new InMemoryRequestAuditStore();
        var recorder = CreateRecorder(auditStore: auditStore);
        double testReward = 0.85;

        // Act
        recorder.RecordAudit(
            requestId: "req-123",
            model: "gpt-3.5-turbo",
            estimatedTokens: 1000,
            usage: null,
            cost: 0.01m,
            latencyMs: 500,
            sessionId: "session-1",
            routingReason: "test",
            success: true,
            errorMessage: null,
            isStreaming: false,
            routedTier: ModelTier.Cheap,
            reward: testReward,
            epsilonPromotedModel: null
        );

        // Assert
        var records = auditStore.GetRecent(1);
        Assert.Single(records);
        Assert.Equal(testReward, records[0].Reward);
    }

    [Fact]
    public void RecordAudit_PassesEpsilonPromotedModelToRecord()
    {
        // Arrange
        var auditStore = new InMemoryRequestAuditStore();
        var recorder = CreateRecorder(auditStore: auditStore);
        string? promotedModel = "gpt-4";

        // Act
        recorder.RecordAudit(
            requestId: "req-123",
            model: "gpt-3.5-turbo",
            estimatedTokens: 1000,
            usage: null,
            cost: 0.01m,
            latencyMs: 500,
            sessionId: "session-1",
            routingReason: "test",
            success: true,
            errorMessage: null,
            isStreaming: false,
            routedTier: ModelTier.Cheap,
            reward: null,
            epsilonPromotedModel: promotedModel
        );

        // Assert
        var records = auditStore.GetRecent(1);
        Assert.Single(records);
        Assert.Equal(promotedModel, records[0].EpsilonPromotedModel);
    }

    [Fact]
    public void RecordAudit_WithoutReward_PassesNull()
    {
        // Arrange
        var auditStore = new InMemoryRequestAuditStore();
        var recorder = CreateRecorder(auditStore: auditStore);

        // Act: 不传 reward 参数（默认 null）
        recorder.RecordAudit(
            requestId: "req-123",
            model: "gpt-3.5-turbo",
            estimatedTokens: 1000,
            usage: null,
            cost: 0.01m,
            latencyMs: 500,
            sessionId: "session-1",
            routingReason: "test",
            success: true,
            errorMessage: null,
            isStreaming: false,
            routedTier: ModelTier.Cheap
        );

        // Assert
        var records = auditStore.GetRecent(1);
        Assert.Single(records);
        Assert.Null(records[0].Reward);
    }

    private static ModelEndpointOptions FakeModel(string name, ModelTier tier) =>
        new()
        {
            Name = name,
            Tier = tier,
            BaseUrl = "https://fake.example.com",
            InputPricePerMillion = 0.5m,
            OutputPricePerMillion = 1.5m,
            MaxContextTokens = 4096,
            Enabled = true
        };
}
