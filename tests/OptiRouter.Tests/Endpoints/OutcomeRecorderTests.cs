using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Mesh;
using OptiRouter.Routing;
using System.Text.Json;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

public class OutcomeRecorderTests
{
    private static OutcomeRecorder CreateRecorder(RoutingOptions? routing = null)
    {
        var options = new RouterOptions { Routing = routing ?? new RoutingOptions() };
        return new OutcomeRecorder(
            auditStore: null!,
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
    public void RecordAudit_NullRequestId_FallsBackToHttpContextItemId()
    {
        // 回归：ProxyOrchestrator/FusionRouter 等全部调用点首参传 null，审计表 request_id 恒为空。
        // 回退链与 TraceScope ambient 语义对齐：入口中间件把 X-Request-Id 或生成 GUID 放入 Items["RequestId"]。
        using var auditStore = new InMemoryRequestAuditStore();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Items["RequestId"] = "req-abc-123";
        var recorder = CreateAuditRecorder(auditStore, new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = httpContext });

        recorder.RecordAudit(null, "model-a", 10, null, 0m, 5, null, "test", true, null, false, ModelTier.Medium);

        Assert.Equal("req-abc-123", Assert.Single(auditStore.GetRecent(10)).RequestId);
    }

    [Fact]
    public void RecordAudit_ExplicitRequestId_NotOverriddenByHttpContext()
    {
        using var auditStore = new InMemoryRequestAuditStore();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Items["RequestId"] = "ambient-id";
        var recorder = CreateAuditRecorder(auditStore, new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = httpContext });

        recorder.RecordAudit("explicit-id", "model-a", 10, null, 0m, 5, null, "test", true, null, false, ModelTier.Medium);

        Assert.Equal("explicit-id", Assert.Single(auditStore.GetRecent(10)).RequestId);
    }

    [Fact]
    public void RecordAudit_NoHttpContext_LeavesRequestIdNull()
    {
        using var auditStore = new InMemoryRequestAuditStore();
        var recorder = CreateAuditRecorder(auditStore, accessor: null);

        recorder.RecordAudit(null, "model-a", 10, null, 0m, 5, null, "test", true, null, false, ModelTier.Medium);

        Assert.Null(Assert.Single(auditStore.GetRecent(10)).RequestId);
    }

    private static OutcomeRecorder CreateAuditRecorder(
        InMemoryRequestAuditStore? auditStore = null,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? accessor = null)
    {
        var options = new RouterOptions { Routing = new RoutingOptions() };
        return new OutcomeRecorder(
            auditStore: auditStore ?? new InMemoryRequestAuditStore(),
            metrics: null!,
            ledger: new CostLedger(),
            options: new FakeRouterOptionsMonitor(options),
            affinityCache: new MemoryCache(new MemoryCacheOptions()),
            tsStore: new ThompsonStateStore(),
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance,
            httpContextAccessor: accessor);
    }

    [Theory]
    [InlineData(0.0, 0, 0.5, 0.0)]   // cost=0 时跳过归一化，返回原 reward
    [InlineData(1.0, 0, 0.5, 1.0)]   // cost>0 但 tokens=0 回退绝对花费口径
    [InlineData(0.5, 100, 0.5, 0.0)]  // 长输入：归一化后 pricePerMillion 低，costReward 高
    public void ApplyCostWeight_TokenNormalization_ComputesExpectedReward(
        double reward, int tokens, double weight, decimal cost)
    {
        var options = new RoutingOptions { CostAwareWeight = weight, CostAwareBaselineUsd = 1.0m };
        var result = (double)typeof(OutcomeRecorder)
            .GetMethod("ApplyCostWeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [reward, cost, tokens, options])!;

        if (cost <= 0)
        {
            Assert.Equal(reward, result);
            return;
        }

        if (tokens <= 0)
        {
            // 回退绝对花费口径。
            double normalizedCost = (double)cost;
            double costReward = 1.0 / (1.0 + normalizedCost);
            Assert.Equal((1.0 - weight) * reward + weight * costReward, result, precision: 5);
            return;
        }

        double expectedNormalized = (double)cost * 1_000_000.0 / tokens;
        double expectedCostReward = 1.0 / (1.0 + expectedNormalized);
        double expected = (1.0 - weight) * reward + weight * expectedCostReward;
        Assert.Equal(expected, result, precision: 10);
    }

    [Fact]
    public void ExtractQualityFactor_NullResponse_ReturnsOne()
    {
        double factor = OutcomeRecorder.ExtractQualityFactor(null, penalty: 0.3);
        Assert.Equal(1.0, factor);
    }

    [Fact]
    public void ExtractQualityFactor_FinishReasonLength_ReturnsPenalty()
    {
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"length\"}]}", Usage: null);
        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3);
        Assert.Equal(0.3, factor);
    }

    [Fact]
    public void ExtractQualityFactor_EmptyContent_ReturnsPenalty()
    {
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":\"stop\"}]}", Usage: null);
        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3);
        Assert.Equal(0.3, factor);
    }

    [Fact]
    public void ExtractQualityFactor_ValidJsonResponse_ReturnsOne()
    {
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"ok\\\":true}\"},\"finish_reason\":\"stop\"}]}", Usage: null);
        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3);
        Assert.Equal(1.0, factor);
    }

    [Fact]
    public void ExtractQualityFactor_JsonContractViolation_ReturnsPenalty()
    {
        // 请求显式要求 JSON，但响应内容不是合法 JSON。
        var request = new ChatRequest
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" })
            }
        };
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"not json\"},\"finish_reason\":\"stop\"}]}", Usage: null);

        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3, request: request);
        Assert.Equal(0.3, factor);
    }

    [Fact]
    public void ExtractQualityFactor_JsonContractViolation_WithFencedJson_ReturnsOne()
    {
        // 模型在 JSON 外围加了 ```json 围栏，应剥除后通过。
        var request = new ChatRequest
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" })
            }
        };
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"```json\\n{\\\"ok\\\":true}\\n```\"},\"finish_reason\":\"stop\"}]}", Usage: null);

        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3, request: request);
        Assert.Equal(1.0, factor);
    }

    [Fact]
    public void ExtractQualityFactor_NoJsonRequest_IgnoresInvalidJson()
    {
        // 未显式要求 JSON 时，即使 content 非法 JSON 也不惩罚。
        var response = new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"not json\"},\"finish_reason\":\"stop\"}]}", Usage: null);

        double factor = OutcomeRecorder.ExtractQualityFactor(response, penalty: 0.3, request: new ChatRequest());
        Assert.Equal(1.0, factor);
    }

    [Fact]
    public void MapLatencyToReward_NullElapsed_ReturnsZero()
    {
        Assert.Equal(0.0, OutcomeRecorder.MapLatencyToReward(null, targetMs: 1000));
    }

    [Theory]
    [InlineData(0, 1000, 1.0)]
    [InlineData(500, 1000, 0.85)]
    [InlineData(1000, 1000, 0.7)]
    [InlineData(1500, 1000, 0.5)]
    [InlineData(2000, 1000, 0.3)]
    [InlineData(3000, 1000, 0.3)]
    public void MapLatencyToReward_MonotonicMapping(long elapsed, double target, double expected)
    {
        double reward = OutcomeRecorder.MapLatencyToReward(elapsed, target);
        Assert.Equal(expected, reward, precision: 10);
    }

    [Theory]
    [InlineData(ModelTier.Strong, 5000, 5000)]
    [InlineData(ModelTier.Medium, 2000, 2000)]
    [InlineData(null, 1000, 1000)]
    public void ResolveLatencyTarget_UsesTierTargetWhenSet(ModelTier? actualTier, double globalTarget, double expected)
    {
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = globalTarget,
            ThompsonLatencyTargetMsByTier = new Dictionary<ModelTier, double>
            {
                [ModelTier.Strong] = 5000,
                [ModelTier.Medium] = 2000
            }
        };

        double resolved = OutcomeRecorder.ResolveLatencyTarget(actualTier, routing);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void EstimateInputCost_ReturnsZeroForNonPositiveTokens()
    {
        var model = new ModelEndpointOptions { InputPricePerMillion = 10m };
        Assert.Equal(0m, OutcomeRecorder.EstimateInputCost(model, estimatedTokens: 0));
        Assert.Equal(0m, OutcomeRecorder.EstimateInputCost(model, estimatedTokens: -1));
    }

    [Fact]
    public void EstimateInputCost_ComputesInputCost()
    {
        var model = new ModelEndpointOptions { InputPricePerMillion = 10m };
        // 100 tokens @ $10/M = 100 * 10 / 1_000_000 = 0.001
        Assert.Equal(0.001m, OutcomeRecorder.EstimateInputCost(model, estimatedTokens: 100));
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    [InlineData("POSTGRES")]
    [InlineData("Redis")]
    [InlineData("redis")]
    [InlineData("REDIS")]
    public void RecordCost_SharedStore_DoesNotBroadcastCost(string storeProvider)
    {
        var fixture = CreateCostRecorder(storeProvider);
        using var synchronizer = fixture.Synchronizer;

        fixture.Recorder.RecordCost(1.25m, "session-1");

        Assert.Equal(1.25m, fixture.Ledger.GetSpend().Total);
        Assert.Equal(0, fixture.Mesh.GetStats().PublishedEventsCount);
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("InMemory")]
    public void RecordCost_LocalStore_BroadcastsCostOnce(string storeProvider)
    {
        var fixture = CreateCostRecorder(storeProvider);
        using var synchronizer = fixture.Synchronizer;

        fixture.Recorder.RecordCost(1.25m, "session-1");

        Assert.Equal(1.25m, fixture.Ledger.GetSpend().Total);
        Assert.Equal(1, fixture.Mesh.GetStats().PublishedEventsCount);
    }

    [Fact]
    public void RecordThompsonOutcome_DefaultConfig_NoNormalization()
    {
        // 默认配置（refTokens=0）行为与改动前完全一致：不归一化
        var routing = new RoutingOptions { ThompsonLatencyNormalizeRefTokens = 0 };
        var options = new RouterOptions { Routing = routing };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = false,
            RequestMessageCount = 1
        };

        // 非流式，无 TTFT，completionTokens=2000，但 refTokens=0 时不归一化
        double reward = recorder.RecordThompsonOutcome("m1", 2000L, decision, cost: 0m, completionTokens: 2000);

        // 应该用原始 2000ms 映射 reward
        double expected = OutcomeRecorder.MapLatencyToReward(2000L, routing.ThompsonLatencyTargetMs);
        Assert.Equal(expected, reward, precision: 10);
    }

    [Fact]
    public void RecordThompsonOutcome_WithOutputNormalization_ReducesEffectiveLatency()
    {
        // refTokens=500、completionTokens=2000、elapsed=2000ms → 有效延迟 500ms
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = 1000,
            ThompsonLatencyNormalizeRefTokens = 500
        };
        var options = new RouterOptions { Routing = routing };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = false,
            RequestMessageCount = 1
        };

        // 非流式，有 completionTokens，无 TTFT
        double reward = recorder.RecordThompsonOutcome("m1", 2000L, decision, cost: 0m, completionTokens: 2000);

        // 有效延迟应为 500ms（2000 * 500 / 2000）
        double expected = OutcomeRecorder.MapLatencyToReward(500L, routing.ThompsonLatencyTargetMs);
        Assert.Equal(expected, reward, precision: 10);

        // 验证 reward 高于未归一化时的值（未归一化时 2000ms 映射到更低 reward）
        double withoutNorm = OutcomeRecorder.MapLatencyToReward(2000L, routing.ThompsonLatencyTargetMs);
        Assert.True(reward > withoutNorm, $"归一化后 reward {reward} 应高于未归一化 {withoutNorm}");
    }

    [Fact]
    public void RecordThompsonOutcome_CompletionTokensBelowRef_NoNormalization()
    {
        // completionTokens < refTokens 时不归一化
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = 1000,
            ThompsonLatencyNormalizeRefTokens = 500
        };
        var options = new RouterOptions { Routing = routing };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = false,
            RequestMessageCount = 1
        };

        double reward = recorder.RecordThompsonOutcome("m1", 2000L, decision, cost: 0m, completionTokens: 300);

        // 有效延迟仍为 2000ms（300 < 500，不归一化）
        double expected = OutcomeRecorder.MapLatencyToReward(2000L, routing.ThompsonLatencyTargetMs);
        Assert.Equal(expected, reward, precision: 10);
    }

    [Fact]
    public void RecordThompsonOutcome_StreamingWithTTFT_UsesTTFTNotTotalLatency()
    {
        // 流式 + TTFT → 用 TTFT，忽略总耗时
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = 1000,
            ThompsonLatencyNormalizeRefTokens = 500
        };
        var options = new RouterOptions { Routing = routing };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = true,
            RequestMessageCount = 1
        };

        // 流式：总耗时 5000ms，TTFT=300ms，completionTokens=2000
        double reward = recorder.RecordThompsonOutcome("m1", 5000L, decision, cost: 0m, completionTokens: 2000, timeToFirstTokenMs: 300L);

        // 有效延迟应为 300ms（TTFT），不做归一化（流式下输出未完成）
        double expected = OutcomeRecorder.MapLatencyToReward(300L, routing.ThompsonLatencyTargetMs);
        Assert.Equal(expected, reward, precision: 10);
    }

    [Fact]
    public void RecordThompsonOutcome_NonStreamingWithTTFT_IgnoresTTFT()
    {
        // 非流式 + TTFT → 忽略 TTFT，仍用总耗时
        var routing = new RoutingOptions
        {
            ThompsonLatencyTargetMs = 1000,
            ThompsonLatencyNormalizeRefTokens = 500
        };
        var options = new RouterOptions { Routing = routing };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = false,
            RequestMessageCount = 1
        };

        // 非流式：总耗时 2000ms，TTFT=300ms（应被忽略），completionTokens=2000
        double reward = recorder.RecordThompsonOutcome("m1", 2000L, decision, cost: 0m, completionTokens: 2000, timeToFirstTokenMs: 300L);

        // 有效延迟应为 500ms（归一化后），TTFT 被忽略（非流式）
        double expected = OutcomeRecorder.MapLatencyToReward(500L, routing.ThompsonLatencyTargetMs);
        Assert.Equal(expected, reward, precision: 10);
    }

    [Fact]
    public void RecordThompsonOutcome_NullElapsed_ReturnsZero()
    {
        // elapsedMs=null → reward=0（硬失败）
        var routing = new RoutingOptions { ThompsonLatencyTargetMs = 1000 };
        var recorder = CreateRecorder(routing);

        var decision = new RouterDecision
        {
            Reason = "test",
            Candidates = [new ModelEndpointOptions { Name = "m1", Tier = ModelTier.Medium, MaxContextTokens = 10000, BaseUrl = "https://example.com", InputPricePerMillion = 1m, OutputPricePerMillion = 1m }],
            EstimatedInputTokens = 0,
            RequestIsStreaming = false,
            RequestMessageCount = 1
        };

        double reward = recorder.RecordThompsonOutcome("m1", null, decision, cost: 0m, completionTokens: 2000);

        Assert.Equal(0.0, reward);
    }

    private static (OutcomeRecorder Recorder, CostLedger Ledger, RecordingMesh Mesh, DistributedMeshSynchronizer Synchronizer)
        CreateCostRecorder(string storeProvider)
    {
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { StoreProvider = storeProvider },
            Routing = new RoutingOptions
            {
                EnableDistributedStateMesh = true,
                MeshBroadcastCostLedger = true
            }
        };
        var ledger = new CostLedger();
        var mesh = new RecordingMesh();
        var synchronizer = new DistributedMeshSynchronizer(mesh);
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: ledger,
            options: new FakeRouterOptionsMonitor(options),
            affinityCache: null!,
            tsStore: null!,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance,
            meshSynchronizer: synchronizer);

        return (recorder, ledger, mesh, synchronizer);
    }

    private sealed class RecordingMesh : IDistributedStateMesh
    {
        private long _publishedEventsCount;

        public string NodeId => "recording-node";

        public Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _publishedEventsCount);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(string channel, Action<TEvent> onReceived)
            => NoopDisposable.Instance;

        public MeshStats GetStats()
            => new(NodeId, Interlocked.Read(ref _publishedEventsCount), 0, 0);

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
