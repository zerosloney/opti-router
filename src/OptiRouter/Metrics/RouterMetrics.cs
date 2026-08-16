using Prometheus;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Metrics;

/// <summary>
/// 路由代理的 Prometheus 指标集合。单例，所有请求路径共享同一组仪表。
/// 指标命名前缀 <c>optirouter_</c>，标签含 model/tier/outcome/streaming。
/// 仅记录聚合数与模型名，不泄露 API Key 或 PII。
/// </summary>
public sealed class RouterMetrics
{
    /// <summary>请求尝试总数（每次候选尝试计一次，failover 时一次请求多候选多次计数）。</summary>
    private readonly Counter _requestsTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_proxy_requests_total",
        "Total number of model attempts per outcome.",
        new CounterConfiguration
        {
            LabelNames = new[] { "model", "tier", "outcome", "streaming" }
        });

    /// <summary>Token 消耗总数，按输入/输出与模型分。</summary>
    private readonly Counter _tokensTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_tokens_total",
        "Total tokens consumed by direction.",
        new CounterConfiguration
        {
            LabelNames = new[] { "model", "direction" }
        });

    /// <summary>累计美元成本。</summary>
    private readonly Counter _costUsdTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_cost_usd_total",
        "Total USD cost accumulated.",
        new CounterConfiguration
        {
            LabelNames = new[] { "model" }
        });

    /// <summary>请求尝试延迟直方图（毫秒）。</summary>
    private readonly Histogram _durationMs = Prometheus.Metrics.CreateHistogram(
        "optirouter_request_duration_ms",
        "Latency of each model attempt in milliseconds.",
        new HistogramConfiguration
        {
            LabelNames = new[] { "model", "streaming" },
            Buckets = Histogram.ExponentialBuckets(50, 2, 12) // 50ms .. ~200s
        });

    private readonly Histogram _ttftMs = Prometheus.Metrics.CreateHistogram(
        "optirouter_time_to_first_token_ms",
        "Time to first upstream SSE data item, or response-header proxy for non-streaming attempts.",
        new HistogramConfiguration
        {
            LabelNames = new[] { "model", "streaming" },
            Buckets = Histogram.ExponentialBuckets(25, 2, 13)
        });

    private readonly Counter _cacheTokensTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_cache_tokens_total",
        "Input token breakdown by normalized cache kind.",
        new CounterConfiguration { LabelNames = new[] { "model", "kind" } });

    private readonly Counter _quotaLimitedTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_quota_limited_total",
        "Upstream attempts rejected because of provider quota.",
        new CounterConfiguration { LabelNames = new[] { "model" } });

    /// <summary>断路器累计失败数（gauge，按模型）。</summary>
    private readonly Gauge _circuitFailureCount = Prometheus.Metrics.CreateGauge(
        "optirouter_circuit_failure_count",
        "Current consecutive failure count recorded by the circuit breaker.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "model" }
        });

    /// <summary>当日花费（美元）。</summary>
    private readonly Gauge _dailySpendUsd = Prometheus.Metrics.CreateGauge(
        "optirouter_daily_spend_usd",
        "Total USD spend since the current UTC day began.");

    /// <summary>累计花费（美元，进程生命周期）。</summary>
    private readonly Gauge _totalSpendUsd = Prometheus.Metrics.CreateGauge(
        "optirouter_total_spend_usd",
        "Total USD spend since process start.");

    private readonly Counter _promptCompressionSavedTokens = Prometheus.Metrics.CreateCounter(
        "optirouter_prompt_compression_saved_tokens_total",
        "Total tokens saved by adaptive prompt compression.");

    private readonly Counter _mcpSanitizationsTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_mcp_tool_sanitizations_total",
        "Total MCP tool arguments repaired and sanitized.",
        new CounterConfiguration { LabelNames = new[] { "tool", "issue" } });

    private readonly Counter _meshSyncEventsTotal = Prometheus.Metrics.CreateCounter(
        "optirouter_mesh_sync_events_total",
        "Total Distributed State Mesh synchronization events.",
        new CounterConfiguration { LabelNames = new[] { "event_type" } });

    /// <summary>记录提示词压缩节省的 Token 数量。</summary>
    public void RecordPromptCompression(int savedTokens)
    {
        if (savedTokens > 0)
        {
            _promptCompressionSavedTokens.Inc(savedTokens);
        }
    }

    /// <summary>记录 MCP 工具参数修复事件。</summary>
    public void RecordMcpSanitization(string toolName, string issueType)
    {
        _mcpSanitizationsTotal.WithLabels(toolName ?? "unknown", issueType ?? "syntax_repair").Inc();
    }

    /// <summary>记录 Mesh 集群状态同步事件。</summary>
    public void RecordMeshSync(string eventType)
    {
        _meshSyncEventsTotal.WithLabels(eventType ?? "state_update").Inc();
    }

    /// <summary>
    /// 记录一次模型尝试结果。由 ProxyOrchestrator.RecordAudit 在所有成功/失败路径统一调用。
    /// </summary>
    /// <param name="model">模型名。</param>
    /// <param name="tier">本轮路由命中分档。</param>
    /// <param name="success">是否成功。</param>
    /// <param name="errorMessage">失败时的错误信息（用于推导 outcome），成功时为 null。</param>
    /// <param name="isStreaming">是否流式请求。</param>
    /// <param name="latencyMs">本次尝试延迟（毫秒）。</param>
    /// <param name="promptTokens">输入 token 数（成功时有真实值，失败时为 0）。</param>
    /// <param name="completionTokens">输出 token 数（成功时有真实值，失败时为 0）。</param>
    /// <param name="cost">本次成本（美元）。</param>
    /// <param name="timeToFirstTokenMs">TTFT 或非流式响应头延迟代理。</param>
    /// <param name="cachedInputTokens">缓存命中输入 token。</param>
    /// <param name="cacheWriteInputTokens">缓存写入输入 token。</param>
    /// <param name="uncachedInputTokens">未缓存输入 token。</param>
    /// <param name="quotaLimited">是否为上游 429 配额拒绝。</param>
    public void RecordAttempt(
        string model,
        ModelTier tier,
        bool success,
        string? errorMessage,
        bool isStreaming,
        long latencyMs,
        int promptTokens,
        int completionTokens,
        decimal cost,
        long? timeToFirstTokenMs = null,
        int cachedInputTokens = 0,
        int cacheWriteInputTokens = 0,
        int uncachedInputTokens = 0,
        bool quotaLimited = false)
    {
        string outcome = DeriveOutcome(success, errorMessage);
        string streamingLabel = isStreaming ? "true" : "false";

        _requestsTotal.WithLabels(model, tier.ToString(), outcome, streamingLabel).Inc();
        _durationMs.WithLabels(model, streamingLabel).Observe(latencyMs);
        if (timeToFirstTokenMs is >= 0)
            _ttftMs.WithLabels(model, streamingLabel).Observe(timeToFirstTokenMs.Value);

        if (promptTokens > 0)
            _tokensTotal.WithLabels(model, "input").Inc(promptTokens);
        if (completionTokens > 0)
            _tokensTotal.WithLabels(model, "output").Inc(completionTokens);
        if (cost > 0m)
            _costUsdTotal.WithLabels(model).Inc((double)cost);
        if (cachedInputTokens > 0)
            _cacheTokensTotal.WithLabels(model, "hit").Inc(cachedInputTokens);
        if (cacheWriteInputTokens > 0)
            _cacheTokensTotal.WithLabels(model, "write").Inc(cacheWriteInputTokens);
        if (uncachedInputTokens > 0)
            _cacheTokensTotal.WithLabels(model, "uncached").Inc(uncachedInputTokens);
        if (quotaLimited)
            _quotaLimitedTotal.WithLabels(model).Inc();
    }

    /// <summary>
    /// 刷新后台聚合型 gauge（花费、断路器）。由 <see cref="OptiRouter.Health.MetricsGaugeUpdaterService"/> 周期调用，
    /// 也可在 scrape 前手动触发。零定时器：复用探活周期。
    /// </summary>
    /// <param name="ledger">成本账本。</param>
    /// <param name="healthTracker">模型健康跟踪器。</param>
    public void RefreshStateGauges(CostLedger ledger, ModelHealthTracker healthTracker)
    {
        var spend = ledger.GetSpend();
        _dailySpendUsd.Set((double)spend.Daily);
        _totalSpendUsd.Set((double)spend.Total);

        foreach (var (model, info) in healthTracker.GetCircuitsSnapshot())
        {
            _circuitFailureCount.WithLabels(model).Set(info.FailureCount);
        }
    }

    /// <summary>从成功标志与错误信息推导标准化 outcome 标签。</summary>
    private static string DeriveOutcome(bool success, string? errorMessage)
    {
        if (success) return "success";
        if (string.IsNullOrEmpty(errorMessage)) return "error";
        if (errorMessage.Contains("quota", StringComparison.OrdinalIgnoreCase)) return "quota_exhausted";
        // 与审计 errorMessage 关键词对齐：timeout / network / stream-faulted / HTTP 状态等。
        if (errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "timeout";
        if (errorMessage.Contains("stream-faulted", StringComparison.OrdinalIgnoreCase)) return "stream_error";
        return "model_error";
    }
}
