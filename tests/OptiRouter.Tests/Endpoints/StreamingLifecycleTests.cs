using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// P1-6 回归：流式外部取消的终态核算。修复前 MoveNextAsync 的所有异常都置 streamFaulted，
/// 客户端取消会记入上游熔断统计（RecordFailure + Thompson 负反馈），且 usage 行已到达的
/// 消费不结算（失败审计写 null usage、0 成本）。
/// 修复后：取消/断开不进熔断、探测槽位释放、已知 usage 一次性结算，审计记 client-cancelled。
/// </summary>
public sealed class StreamingLifecycleTests
{
    private const string ModelName = "cancel-model";

    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = ModelName,
        BaseUrl = "http://localhost/v1",
        ApiKey = "test-only",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        Enabled = true,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m
    };

    private static void ConfigureOptions(RouterOptions options)
    {
        options.Models.Clear();
        options.Models.Add(CreateEndpoint());
        options.Routing.EnableRuleClassifier = false;
        options.Routing.EnableTokenEstimator = false;
        options.Routing.EnableBudgetGuard = false;
        // failover 开启：预流阶段 TryBeginProbe 会为模型建立健康条目并占用探测槽位，
        // 使"取消不记失败、槽位不泄漏"的断言有可观察对象。
        options.Routing.EnableFailover = true;
        options.Routing.EnableSemanticRouter = false;
        options.Routing.EnableSessionAffinity = false;
        options.Routing.EnableLoadBalance = false;
        options.Routing.EnableResponseCache = false;
        options.Routing.EnableSemanticCache = false;
        options.Routing.EnableHealthProbe = false;
        options.Routing.EnableLatencyAware = false;
    }

    private static RawStreamLine Delta(string text) => new(
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }),
        Usage: null, Metadata: null);

    private static readonly RawStreamLine UsageLine = new(
        "{\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}",
        new ChatUsage { PromptTokens = 5, CompletionTokens = 2, TotalTokens = 7 });

    /// <summary>
    /// usage 行 + 持续产出：第 4 次 MoveNext 挂起在 release 信号上，测试先取消编排器级
    /// ct 再放行，mock 抛裸 OCE（模拟取消沿传输层传播）。不挂起在编排器传入的 ttft 令牌上
    /// （首行后其来源 CTS 已被 Dispose，再次注册会 ObjectDisposedException）。
    /// </summary>
    private static Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> CancelledUpstreamStream(TaskCompletionSource release)
    {
        return (request, ct) => Iterate(release);

        async IAsyncEnumerable<RawStreamLine> Iterate(TaskCompletionSource release, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Delta("Hello");
            yield return Delta(" world");
            yield return UsageLine;
            await release.Task;
            throw new OperationCanceledException("upstream read cancelled by client");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>usage 行 + 上游真实故障（非取消异常）：中途断连。</summary>
    private static Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> FaultedUpstreamStream()
    {
        return (request, ct) => Iterate();

        async IAsyncEnumerable<RawStreamLine> Iterate([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Delta("partial");
            yield return UsageLine;
            await Task.Yield();
            throw new HttpRequestException("upstream connection reset mid-stream");
        }
    }

    private static TestWebApplicationFactory CreateFactory(Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> streamFunc)
    {
        var endpoint = CreateEndpoint();
        return new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(ConfigureOptions),
            MockClients = { [ModelName] = new MockModelClient(endpoint, streamRawFunc: streamFunc) }
        };
    }

    /// <summary>客户端取消：不增加上游失败计数、探测槽位释放、已知 usage 一次性结算不丢失。</summary>
    [Fact]
    public async Task Streaming_ClientCancel_DoesNotTripCircuit_AndSettlesKnownUsage()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = CreateFactory(CancelledUpstreamStream(release));
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var request = new ChatRequest { Model = ModelName, Messages = [ChatMessage.FromText("user", "Hi")] };

        var cts = new CancellationTokenSource();
        var enumerator = orchestrator.StreamAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync()); // delta "Hello"
        Assert.True(await enumerator.MoveNextAsync()); // delta " world"
        Assert.True(await enumerator.MoveNextAsync()); // usage 行

        cts.Cancel();        // 编排器级取消先行：catch 过滤条件 ct.IsCancellationRequested 成立
        release.SetResult(); // 解除 mock 挂起，抛出 OCE
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();

        // 取消不得污染上游健康：修复前 streamFaulted 路径会 RecordFailure 建条目（FailureCount=1、
        // 计入熔断）；修复后无失败记录（闭合态快速路径不建条目，快照可能无该模型）。
        var circuits = factory.Services.GetRequiredService<ModelHealthTracker>().GetCircuitsSnapshot();
        if (circuits.TryGetValue(ModelName, out var circuit))
        {
            Assert.Equal(CircuitState.Closed, circuit.State);
            Assert.Equal(0, circuit.FailureCount);
            Assert.Equal(0, circuit.ActiveProbes);
        }

        // 已取得的 usage 一次性结算（5×1 + 2×2 / 1e6），既不丢失也不双记。
        var spend = factory.Services.GetRequiredService<CostLedger>().GetDailySpend();
        Assert.Equal((5m * 1m + 2m * 2m) / 1_000_000m, spend);

        // 审计经后台批量写落库，轮询等待可见（与 MariaDB 集成测试同口径）。
        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        var row = await WaitForAuditAsync(auditStore, a => a.Model == ModelName && a.ErrorMessage == "client-cancelled");
        Assert.Equal(5, row.PromptTokens);
        Assert.Equal(2, row.CompletionTokens);
        Assert.Equal((5m * 1m + 2m * 2m) / 1_000_000m, row.Cost);
    }

    /// <summary>上游中途真实故障（非取消）：维持既有 streamFaulted 语义——计入熔断、不按 usage 记账。</summary>
    [Fact]
    public async Task Streaming_UpstreamFault_StillCountsAsFailure()
    {
        using var factory = CreateFactory(FaultedUpstreamStream());
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var request = new ChatRequest { Model = ModelName, Messages = [ChatMessage.FromText("user", "Hi")] };

        var enumerator = orchestrator.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync()); // delta "partial"
        Assert.True(await enumerator.MoveNextAsync()); // usage 行
        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();

        var circuits = factory.Services.GetRequiredService<ModelHealthTracker>().GetCircuitsSnapshot();
        var circuit = Assert.Single(circuits, c => c.Key == ModelName).Value;
        Assert.Equal(1, circuit.FailureCount); // 上游故障仍计入断路器
        Assert.Equal(0, circuit.ActiveProbes);

        await WaitForAuditAsync(factory.Services.GetRequiredService<IRequestAuditStore>(),
            a => a.Model == ModelName && a.ErrorMessage == "stream-faulted");
    }

    /// <summary>首行前即抛出给定异常的流回调（模拟首行前上游失败，StreamRawAsync 调用时同步抛出）。</summary>
    private static Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> ThrowingStream(Exception error)
        => (_, _) => throw error;

    /// <summary>首行前配额拒绝（429）：不熔断、探槽释放、审计记 quota-exhausted 且 isStreaming=true
    /// （流式首行前失败与串行路径共用 SettleCandidateFailure 后的统一语义）。</summary>
    [Fact]
    public async Task Streaming_PreStreamQuota_NoCircuitFailure_RecordsQuotaAudit()
    {
        using var factory = CreateFactory(ThrowingStream(new ModelClientException(
            System.Net.HttpStatusCode.TooManyRequests, "rate limited", "quota exhausted")));
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var request = new ChatRequest { Model = ModelName, Messages = [ChatMessage.FromText("user", "Hi")] };

        var enumerator = orchestrator.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<AllCandidatesFailedException>(
            async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();

        // 429 是纯配额：不入断路器，探槽无泄漏。
        var circuits = factory.Services.GetRequiredService<ModelHealthTracker>().GetCircuitsSnapshot();
        if (circuits.TryGetValue(ModelName, out var circuit))
        {
            Assert.Equal(CircuitState.Closed, circuit.State);
            Assert.Equal(0, circuit.FailureCount);
            Assert.Equal(0, circuit.ActiveProbes);
        }

        var row = await WaitForAuditAsync(factory.Services.GetRequiredService<IRequestAuditStore>(),
            a => a.Model == ModelName && a.ErrorMessage == "quota-exhausted");
        Assert.True(row.IsStreaming);
        Assert.True(row.QuotaLimited);
        Assert.False(row.Success);
    }

    /// <summary>首行前上游状态错误（503）：计入熔断、审计记 upstream-status-503 且 isStreaming=true。</summary>
    [Fact]
    public async Task Streaming_PreStreamUpstreamError_CountsCircuitFailure()
    {
        using var factory = CreateFactory(ThrowingStream(new ModelClientException(
            System.Net.HttpStatusCode.ServiceUnavailable, "upstream down")));
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var request = new ChatRequest { Model = ModelName, Messages = [ChatMessage.FromText("user", "Hi")] };

        var enumerator = orchestrator.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<AllCandidatesFailedException>(
            async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();

        var circuits = factory.Services.GetRequiredService<ModelHealthTracker>().GetCircuitsSnapshot();
        var circuit = Assert.Single(circuits, c => c.Key == ModelName).Value;
        Assert.Equal(1, circuit.FailureCount);
        Assert.Equal(0, circuit.ActiveProbes);

        var row = await WaitForAuditAsync(factory.Services.GetRequiredService<IRequestAuditStore>(),
            a => a.Model == ModelName && a.ErrorMessage == "upstream-status-503");
        Assert.True(row.IsStreaming);
        Assert.False(row.QuotaLimited);
    }

    /// <summary>审计为后台批量落库，轮询 GetRecent 直到目标行可见（上限 10s）。</summary>
    private static async Task<RequestAuditRecord> WaitForAuditAsync(
        IRequestAuditStore store, Func<RequestAuditRecord, bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            var row = store.GetRecent(50).FirstOrDefault(predicate);
            if (row is not null)
                return row;
            await Task.Delay(100);
        }

        Assert.Fail("audit row did not become visible within 10s");
        return null;
    }
}
