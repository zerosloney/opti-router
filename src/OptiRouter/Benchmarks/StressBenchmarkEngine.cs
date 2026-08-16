using System.Diagnostics;
using OptiRouter.Clients;

namespace OptiRouter.Benchmarks;

/// <summary>
/// 高并发全链路基准压测与性能评估引擎 (High-Concurrency Stress Benchmark Engine)。
/// 支持模拟大规模并发连接，精确统计纳秒/微秒级延迟分布 ($P_{50}, P_{75}, P_{90}, P_{95}, P_{99}, P_{99.9}$)、
/// TTFT（首字延迟）、RPS（每秒请求吞吐）、TPS（每秒 Token 吞吐）及 GC 内存分配开销。
/// </summary>
public sealed class StressBenchmarkEngine
{
    /// <summary>
    /// 运行基准压力测试。
    /// </summary>
    /// <param name="config">压测配置参数。</param>
    /// <param name="requestExecutor">单次请求执行委托，返回样本指标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>综合压测报告。</returns>
    public async Task<BenchmarkSummaryReport> RunAsync(
        BenchmarkConfig config,
        Func<ChatRequest, CancellationToken, Task<BenchmarkSample>> requestExecutor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(requestExecutor);

        var request = config.CustomRequest ?? new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Hello OptiRouter, summarize the architecture of distributed systems in 50 words.")
            }
        };

        // 1. 预热阶段 (Warmup)
        if (config.WarmupRequests > 0)
        {
            for (int i = 0; i < config.WarmupRequests; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await requestExecutor(request, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略预热异常
                }
            }
        }

        // 2. 采样基准线捕获
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long memBefore = GC.GetTotalAllocatedBytes(true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        int totalRequests = Math.Max(1, config.TotalRequests);
        int concurrency = Math.Max(1, config.Concurrency);
        var samples = new BenchmarkSample[totalRequests];

        int requestIndex = -1;
        var sw = Stopwatch.StartNew();

        // 3. 并发执行压测
        var workerTasks = new Task[concurrency];
        for (int w = 0; w < concurrency; w++)
        {
            workerTasks[w] = Task.Run(async () =>
            {
                while (true)
                {
                    int idx = Interlocked.Increment(ref requestIndex);
                    if (idx >= totalRequests || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var sample = await requestExecutor(request, cancellationToken).ConfigureAwait(false);
                        samples[idx] = sample;
                    }
                    catch (Exception ex)
                    {
                        samples[idx] = new BenchmarkSample(
                            LatencyMs: 0,
                            TtftMs: null,
                            StatusCode: 500,
                            Success: false,
                            ErrorMessage: ex.Message);
                    }
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workerTasks).ConfigureAwait(false);
        sw.Stop();

        // 4. 内存与 GC 差值计算
        long memAfter = GC.GetTotalAllocatedBytes(true);
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        double totalDurationSec = sw.Elapsed.TotalSeconds;
        int completedCount = Math.Min(requestIndex + 1, totalRequests);

        // 5. 统计与指标聚合
        int successCount = 0;
        int failedCount = 0;
        int totalTokens = 0;
        var latencies = new List<double>(completedCount);
        var ttfts = new List<double>(completedCount);

        for (int i = 0; i < completedCount; i++)
        {
            var s = samples[i];
            if (s.Success)
            {
                successCount++;
                latencies.Add(s.LatencyMs);
                if (s.TtftMs.HasValue)
                {
                    ttfts.Add(s.TtftMs.Value);
                }
                totalTokens += s.TokensGenerated;
            }
            else
            {
                failedCount++;
            }
        }

        var latencyPercentiles = CalculatePercentiles(latencies);
        var ttftPercentiles = ttfts.Count > 0 ? CalculatePercentiles(ttfts) : null;

        double rps = totalDurationSec > 0 ? completedCount / totalDurationSec : 0.0;
        double tps = totalDurationSec > 0 ? totalTokens / totalDurationSec : 0.0;

        return new BenchmarkSummaryReport
        {
            Scenario = config.Scenario,
            Concurrency = concurrency,
            TotalRequests = completedCount,
            SuccessRequests = successCount,
            FailedRequests = failedCount,
            TotalDurationSeconds = totalDurationSec,
            RequestsPerSecond = rps,
            TokensPerSecond = tps,
            Latency = latencyPercentiles,
            Ttft = ttftPercentiles,
            TotalAllocatedBytes = Math.Max(0, memAfter - memBefore),
            Gen0Collections = Math.Max(0, gen0After - gen0Before),
            Gen1Collections = Math.Max(0, gen1After - gen1Before),
            Gen2Collections = Math.Max(0, gen2After - gen2Before)
        };
    }

    /// <summary>
    /// 计算数值序列的精确百分位统计值。
    /// </summary>
    public static LatencyPercentiles CalculatePercentiles(List<double> values)
    {
        if (values.Count == 0)
        {
            return new LatencyPercentiles(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        values.Sort();

        double min = values[0];
        double max = values[^1];
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            sum += values[i];
        }
        double mean = sum / values.Count;

        double varianceSum = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            double diff = values[i] - mean;
            varianceSum += diff * diff;
        }
        double stdDev = Math.Sqrt(varianceSum / values.Count);

        return new LatencyPercentiles(
            MinMs: min,
            MeanMs: mean,
            P50Ms: GetPercentile(values, 0.50),
            P75Ms: GetPercentile(values, 0.75),
            P90Ms: GetPercentile(values, 0.90),
            P95Ms: GetPercentile(values, 0.95),
            P99Ms: GetPercentile(values, 0.99),
            P999Ms: GetPercentile(values, 0.999),
            MaxMs: max,
            StdDevMs: stdDev);
    }

    private static double GetPercentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 1) return sortedValues[0];

        double rank = percentile * (sortedValues.Count - 1);
        int lowIndex = (int)Math.Floor(rank);
        int highIndex = (int)Math.Ceiling(rank);

        if (lowIndex == highIndex)
        {
            return sortedValues[lowIndex];
        }

        double fraction = rank - lowIndex;
        return sortedValues[lowIndex] + fraction * (sortedValues[highIndex] - sortedValues[lowIndex]);
    }
}
