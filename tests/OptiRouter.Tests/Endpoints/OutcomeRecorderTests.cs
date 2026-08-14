using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
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
}
