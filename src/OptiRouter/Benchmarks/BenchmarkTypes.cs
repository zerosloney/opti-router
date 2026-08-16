using System.Text;
using OptiRouter.Clients;

namespace OptiRouter.Benchmarks;

/// <summary>
/// 压测目标业务场景类型。
/// </summary>
public enum BenchmarkScenario
{
    /// <summary>
    /// 纯策略链路由决策 (Pure Router Engine Decision)
    /// </summary>
    FastRouteDecision,

    /// <summary>
    /// 端到端非流式代理调用 (End-to-End Non-Streaming Proxy)
    /// </summary>
    NonStreamingPipeline,

    /// <summary>
    /// 端到端 SSE 增量流式代理调用 (End-to-End Streaming SSE Proxy)
    /// </summary>
    StreamingSsePipeline,

    /// <summary>
    /// MCP 复杂工具调用分析与自愈清洗 (MCP Tool Complexity and Sanitization)
    /// </summary>
    McpComplexityPipeline,

    /// <summary>
    /// RAG 检索上下文密度与充分度分析 (RAG Density Analysis)
    /// </summary>
    RagDensityPipeline
}

/// <summary>
/// 压测运行参数配置。
/// </summary>
public sealed class BenchmarkConfig
{
    /// <summary>
    /// 场景类型。
    /// </summary>
    public BenchmarkScenario Scenario { get; init; } = BenchmarkScenario.FastRouteDecision;

    /// <summary>
    /// 并发工作者数量（并发虚拟用户数）。默认 50。
    /// </summary>
    public int Concurrency { get; init; } = 50;

    /// <summary>
    /// 总压测请求数量。默认 1,000。
    /// </summary>
    public int TotalRequests { get; init; } = 1000;

    /// <summary>
    /// 预热请求数量（不计入正式统计，用于 JIT 与连接池热身）。默认 50。
    /// </summary>
    public int WarmupRequests { get; init; } = 50;

    /// <summary>
    /// 压测使用的测试请求负载。
    /// </summary>
    public ChatRequest? CustomRequest { get; init; }
}

/// <summary>
/// 单次压测样本记录。
/// </summary>
public readonly record struct BenchmarkSample(
    double LatencyMs,
    double? TtftMs,
    int StatusCode,
    bool Success,
    int TokensGenerated = 0,
    string? ErrorMessage = null);

/// <summary>
/// 延迟分位数统计指标。
/// </summary>
public sealed record LatencyPercentiles(
    double MinMs,
    double MeanMs,
    double P50Ms,
    double P75Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs,
    double StdDevMs);

/// <summary>
/// 压测结果综合报告。
/// </summary>
public sealed class BenchmarkSummaryReport
{
    public BenchmarkScenario Scenario { get; init; }
    public int Concurrency { get; init; }
    public int TotalRequests { get; init; }
    public int SuccessRequests { get; init; }
    public int FailedRequests { get; init; }
    public double TotalDurationSeconds { get; init; }
    public double RequestsPerSecond { get; init; }
    public double TokensPerSecond { get; init; }
    public LatencyPercentiles Latency { get; init; } = null!;
    public LatencyPercentiles? Ttft { get; init; }
    public long TotalAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }

    /// <summary>
    /// 格式化为 Markdown 报告。
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# OptiRouter Benchmark Report: {Scenario}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Concurrency (Workers)** | `{Concurrency}` |");
        sb.AppendLine($"| **Total Requests** | `{TotalRequests}` |");
        sb.AppendLine($"| **Successful Requests** | `{SuccessRequests}` ({(SuccessRequests * 100.0 / TotalRequests):F2}%) |");
        sb.AppendLine($"| **Failed Requests** | `{FailedRequests}` |");
        sb.AppendLine($"| **Total Duration** | `{TotalDurationSeconds:F3} s` |");
        sb.AppendLine($"| **Throughput (RPS)** | **`{RequestsPerSecond:F2} req/s`** |");
        if (TokensPerSecond > 0)
        {
            sb.AppendLine($"| **Token Throughput (TPS)** | **`{TokensPerSecond:F2} tokens/s`** |");
        }
        sb.AppendLine($"| **Total GC Allocated** | `{(TotalAllocatedBytes / (1024.0 * 1024.0)):F2} MB` |");
        sb.AppendLine($"| **GC Collections (Gen0/1/2)** | `{Gen0Collections} / {Gen1Collections} / {Gen2Collections}` |");
        sb.AppendLine();

        sb.AppendLine("## Latency Distribution (End-to-End)");
        sb.AppendLine();
        sb.AppendLine("| Percentile | Latency (ms) |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Min** | `{Latency.MinMs:F3} ms` |");
        sb.AppendLine($"| **Mean (Avg)** | `{Latency.MeanMs:F3} ms` |");
        sb.AppendLine($"| **P50 (Median)** | `{Latency.P50Ms:F3} ms` |");
        sb.AppendLine($"| **P75** | `{Latency.P75Ms:F3} ms` |");
        sb.AppendLine($"| **P90** | `{Latency.P90Ms:F3} ms` |");
        sb.AppendLine($"| **P95** | `{Latency.P95Ms:F3} ms` |");
        sb.AppendLine($"| **P99** | `{Latency.P99Ms:F3} ms` |");
        sb.AppendLine($"| **P99.9** | `{Latency.P999Ms:F3} ms` |");
        sb.AppendLine($"| **Max** | `{Latency.MaxMs:F3} ms` |");
        sb.AppendLine($"| **StdDev** | `{Latency.StdDevMs:F3} ms` |");

        if (Ttft is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Time-To-First-Token (TTFT) Distribution");
            sb.AppendLine();
            sb.AppendLine("| Percentile | TTFT (ms) |");
            sb.AppendLine("| :--- | :--- |");
            sb.AppendLine($"| **Min** | `{Ttft.MinMs:F3} ms` |");
            sb.AppendLine($"| **Mean** | `{Ttft.MeanMs:F3} ms` |");
            sb.AppendLine($"| **P50** | `{Ttft.P50Ms:F3} ms` |");
            sb.AppendLine($"| **P95** | `{Ttft.P95Ms:F3} ms` |");
            sb.AppendLine($"| **P99** | `{Ttft.P99Ms:F3} ms` |");
            sb.AppendLine($"| **Max** | `{Ttft.MaxMs:F3} ms` |");
        }

        return sb.ToString();
    }
}
