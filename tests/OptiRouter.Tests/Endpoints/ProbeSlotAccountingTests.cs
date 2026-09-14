using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 候选失败结算与 finally 兜底的探槽核算回归：SettleCandidateFailure 已通过
/// RecordFailure/ReleaseProbe 释放本候选探槽，调用点必须置位 outcomeReported，
/// 否则 finally 的 `if (!outcomeReported) ReleaseProbe` 会把同模型另一在途请求的
/// 槽位偷走（ActiveProbes 少计），halfOpenMaxProbes 并发上限被架空。
/// 断言依赖半开双探槽交错：A 结算落地后、B 仍在途时，ActiveProbes 必须恰为 1
/// （修复前为 0）。递减处的 &gt;0 守卫保证终态恒归零，单请求顺序测试无法区分，
/// 故必须用双在途交错构造。
/// </summary>
public sealed class ProbeSlotAccountingTests
{
    private const string ModelName = "probe-slot-model";

    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = ModelName,
        BaseUrl = "http://localhost/v1",
        ApiKey = "test-only",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        Enabled = true
    };

    private static void ConfigureOptions(RouterOptions options)
    {
        options.Models.Clear();
        options.Models.Add(CreateEndpoint());
        options.Routing.EnableRuleClassifier = false;
        options.Routing.EnableTokenEstimator = false;
        options.Routing.EnableBudgetGuard = false;
        options.Routing.EnableFailover = true;
        options.Routing.EnableSemanticRouter = false;
        options.Routing.EnableSessionAffinity = false;
        options.Routing.EnableLoadBalance = false;
        options.Routing.EnableResponseCache = false;
        options.Routing.EnableSemanticCache = false;
        options.Routing.EnableHealthProbe = false;
        options.Routing.EnableLatencyAware = false;
        // 一败即熔断 + 1s 冷却 + 半开双探槽：构造"A 结算时 B 仍在途"的交错。
        options.Routing.FailoverFailureThreshold = 1;
        options.Routing.FailoverCooldownSeconds = 1;
        options.Routing.FailoverHalfOpenMaxProbes = 2;
    }

    [Fact]
    public async Task CandidateFailure_SettlementDoesNotStealInFlightProbeSlot()
    {
        int callCount = 0;
        var holdA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RawChatResponse> CompleteRaw(ChatRequest _, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref callCount);
            if (n == 1)
                throw new ModelClientException(System.Net.HttpStatusCode.InternalServerError, "boom");
            // 第 2/3 次调用分别挂在两个信号上，模拟两个并发在途请求。
            await (n == 2 ? holdA : holdB).Task;
            throw new ModelClientException(System.Net.HttpStatusCode.InternalServerError, "boom");
        }

        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(ConfigureOptions),
            MockClients = { [ModelName] = new MockModelClient(CreateEndpoint(), completeRawFunc: CompleteRaw) }
        };
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var tracker = factory.Services.GetRequiredService<ModelHealthTracker>();
        var request = new ChatRequest { Model = ModelName, Messages = [ChatMessage.FromText("user", "Hi")] };

        // 第一击（闭合态）：失败即熔断打开。
        await Assert.ThrowsAsync<AllCandidatesFailedException>(
            () => orchestrator.SendAsync(request, CancellationToken.None));
        Assert.Equal(CircuitState.Open, tracker.GetState(ModelName));

        // 冷却到期（惰性转半开）：两个并发请求各占一个探槽并挂起。
        await Task.Delay(1200);
        var taskA = orchestrator.SendAsync(request, CancellationToken.None);
        await WaitForProbeCount(tracker, 1);
        var taskB = orchestrator.SendAsync(request, CancellationToken.None);
        await WaitForProbeCount(tracker, 2);

        // 放行其一：半开探测失败 → 重开熔断并释放自己的槽位（2→1）。
        holdA.SetResult();
        var settled = await Task.WhenAny(taskA, taskB);
        await Assert.ThrowsAsync<AllCandidatesFailedException>(() => settled);

        // 另一请求仍在途：其槽位必须健在。修复前 finally 兜底重复释放，ActiveProbes 提前归零。
        Assert.Equal(1, tracker.GetCircuitsSnapshot()[ModelName].ActiveProbes);

        // 放行最后一个：终态归零（递减守卫保证）。
        holdB.SetResult();
        var pending = taskA.IsCompleted ? taskB : taskA;
        await Assert.ThrowsAsync<AllCandidatesFailedException>(() => pending);
        Assert.Equal(0, tracker.GetCircuitsSnapshot()[ModelName].ActiveProbes);
    }

    private static async Task WaitForProbeCount(ModelHealthTracker tracker, int expected)
    {
        for (int i = 0; i < 100; i++)
        {
            if (tracker.GetCircuitsSnapshot().TryGetValue(ModelName, out var circuit) && circuit.ActiveProbes >= expected)
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"探测槽位计数未在限时内达到 {expected}");
    }
}
