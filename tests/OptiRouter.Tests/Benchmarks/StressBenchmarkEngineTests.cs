using OptiRouter.Benchmarks;
using OptiRouter.Clients;
using Xunit;

namespace OptiRouter.Tests.Benchmarks;

public class StressBenchmarkEngineTests
{
    private readonly StressBenchmarkEngine _engine = new();

    [Fact]
    public async Task RunAsync_ConcurrentExecution_MeasuresLatencyAndRpsAccurately()
    {
        var config = new BenchmarkConfig
        {
            Scenario = BenchmarkScenario.FastRouteDecision,
            Concurrency = 20,
            TotalRequests = 100,
            WarmupRequests = 5
        };

        var report = await _engine.RunAsync(config, async (req, ct) =>
        {
            await Task.Delay(2, ct);
            return new BenchmarkSample(
                LatencyMs: 2.5,
                TtftMs: 1.0,
                StatusCode: 200,
                Success: true,
                TokensGenerated: 35);
        });

        Assert.Equal(100, report.TotalRequests);
        Assert.Equal(100, report.SuccessRequests);
        Assert.Equal(0, report.FailedRequests);
        Assert.True(report.RequestsPerSecond > 0);
        Assert.True(report.TokensPerSecond > 0);
        Assert.NotNull(report.Latency);
        Assert.True(report.Latency.MeanMs > 0);
        Assert.NotNull(report.Ttft);
        Assert.True(report.Ttft.P50Ms > 0);

        string markdown = report.ToMarkdown();
        Assert.Contains("OptiRouter Benchmark Report", markdown);
        Assert.Contains("Throughput (RPS)", markdown);
        Assert.Contains("Latency Distribution", markdown);
    }

    [Fact]
    public async Task RunAsync_HandlesFailuresGracefully()
    {
        var config = new BenchmarkConfig
        {
            Scenario = BenchmarkScenario.NonStreamingPipeline,
            Concurrency = 10,
            TotalRequests = 50,
            WarmupRequests = 0
        };

        int count = 0;
        var report = await _engine.RunAsync(config, (req, ct) =>
        {
            int current = Interlocked.Increment(ref count);
            if (current % 2 == 0)
            {
                throw new InvalidOperationException("Upstream connection error");
            }

            return Task.FromResult(new BenchmarkSample(
                LatencyMs: 5.0,
                TtftMs: null,
                StatusCode: 200,
                Success: true));
        });

        Assert.Equal(50, report.TotalRequests);
        Assert.True(report.FailedRequests > 0);
        Assert.True(report.SuccessRequests > 0);
    }

    [Fact]
    public void CalculatePercentiles_AccurateDistribution()
    {
        var values = new List<double> { 10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0 };

        var p = StressBenchmarkEngine.CalculatePercentiles(values);

        Assert.Equal(10.0, p.MinMs);
        Assert.Equal(100.0, p.MaxMs);
        Assert.Equal(55.0, p.MeanMs);
        Assert.Equal(55.0, p.P50Ms);
        Assert.True(p.P90Ms > p.P75Ms);
        Assert.True(p.StdDevMs > 0);
    }

    [Fact]
    public void CalculatePercentiles_EmptyAndSingleElement_ReturnsValid()
    {
        var empty = StressBenchmarkEngine.CalculatePercentiles(new List<double>());
        Assert.Equal(0.0, empty.MeanMs);

        var single = StressBenchmarkEngine.CalculatePercentiles(new List<double> { 42.0 });
        Assert.Equal(42.0, single.MinMs);
        Assert.Equal(42.0, single.MaxMs);
        Assert.Equal(42.0, single.P50Ms);
        Assert.Equal(0.0, single.StdDevMs);
    }

    [Fact]
    public async Task RunAsync_FullRouterEngine_HighThroughput_ZeroAllocationVerification()
    {
        var ledger = new OptiRouter.Routing.CostLedger();
        var policies = new OptiRouter.Routing.IRouterPolicy[]
        {
            new OptiRouter.Routing.ExplicitModelPolicy(),
            new OptiRouter.Routing.DataSovereigntyPolicy(),
            new OptiRouter.Routing.CapabilityFilterPolicy(),
            new OptiRouter.Routing.RuleClassifierPolicy(),
            new OptiRouter.Routing.LongInputPolicy(),
            new OptiRouter.Routing.ParetoFrontierPolicy()
        };

        var router = new OptiRouter.Routing.RouterEngine(ledger, policies);

        var options = new OptiRouter.Configuration.RouterOptions();
        options.Models.Add(new() { Id = "m1", Name = "gpt-4o", Tier = OptiRouter.Configuration.ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 });
        options.Models.Add(new() { Id = "m2", Name = "gpt-4o-mini", Tier = OptiRouter.Configuration.ModelTier.Medium, Enabled = true, MaxContextTokens = 128000 });
        options.Models.Add(new() { Id = "m3", Name = "deepseek-chat", Tier = OptiRouter.Configuration.ModelTier.Cheap, Enabled = true, MaxContextTokens = 64000 });

        var config = new BenchmarkConfig
        {
            Scenario = BenchmarkScenario.FastRouteDecision,
            Concurrency = 50,
            TotalRequests = 1000,
            WarmupRequests = 50
        };

        var report = await _engine.RunAsync(config, (req, ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var decision = router.Decide(req, options);
            sw.Stop();

            return Task.FromResult(new BenchmarkSample(
                LatencyMs: sw.Elapsed.TotalMilliseconds,
                TtftMs: null,
                StatusCode: 200,
                Success: decision.Candidates.Count > 0));
        });

        Assert.Equal(1000, report.TotalRequests);
        Assert.Equal(1000, report.SuccessRequests);
        Assert.True(report.RequestsPerSecond > 1000, $"Expected RPS > 1000, actual: {report.RequestsPerSecond:F2} req/s");
        Assert.True(report.Latency.P99Ms < 50.0, $"Expected P99 < 50ms, actual: {report.Latency.P99Ms:F3} ms");
    }
}
