namespace OptiRouter.Routing;

/// <summary>
/// DAG 拓扑节点描述。
/// </summary>
public sealed record DagSpanNode(
    string SpanId,
    string? ParentSpanId,
    string Model,
    string? FusionRole,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost,
    long LatencyMs,
    bool Success,
    bool IsAdopted,
    string RoutingReason);

/// <summary>
/// 分布式 DAG 链路归因树。
/// </summary>
public sealed class DagTraceTree
{
    public required string TraceId { get; init; }
    public required string? ParallelGroupId { get; init; }
    public decimal TotalCost { get; init; }
    public int TotalPromptTokens { get; init; }
    public int TotalCompletionTokens { get; init; }
    public long MaxLatencyMs { get; init; }
    public int TotalSubSpans => Nodes.Count;
    public IReadOnlyList<DagSpanNode> Nodes { get; init; } = Array.Empty<DagSpanNode>();

    public IReadOnlyDictionary<string, decimal> CostByRole
    {
        get
        {
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                string role = node.FusionRole ?? "primary";
                dict[role] = dict.GetValueOrDefault(role) + node.Cost;
            }
            return dict;
        }
    }
}

/// <summary>
/// 归因与 DAG 拓扑计算引擎。
/// </summary>
public static class DagCostAttributor
{
    /// <summary>
    /// 从审计记录列表中对指定 TraceId / ParallelGroupId 进行 DAG 拓扑与成本归因计算。
    /// </summary>
    public static DagTraceTree BuildTraceTree(string traceId, IEnumerable<RequestAuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(traceId);
        ArgumentNullException.ThrowIfNull(records);

        var matching = records
            .Where(r => string.Equals(r.TraceId, traceId, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(r.ParallelGroupId) && string.Equals(r.ParallelGroupId, traceId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matching.Count == 0)
        {
            return new DagTraceTree
            {
                TraceId = traceId,
                ParallelGroupId = null,
                TotalCost = 0m,
                TotalPromptTokens = 0,
                TotalCompletionTokens = 0,
                MaxLatencyMs = 0,
                Nodes = Array.Empty<DagSpanNode>()
            };
        }

        decimal totalCost = 0m;
        int totalPrompt = 0;
        int totalCompletion = 0;
        long maxLatency = 0;
        string? parallelGroupId = matching[0].ParallelGroupId;

        var nodes = new List<DagSpanNode>(matching.Count);

        foreach (var r in matching)
        {
            totalCost += r.Cost;
            totalPrompt += r.PromptTokens > 0 ? r.PromptTokens : r.EstimatedInputTokens;
            totalCompletion += r.CompletionTokens;
            if (r.LatencyMs > maxLatency)
                maxLatency = r.LatencyMs;

            nodes.Add(new DagSpanNode(
                r.SpanId ?? Guid.NewGuid().ToString("N")[..16],
                r.ParentSpanId,
                r.Model,
                r.FusionRole,
                r.PromptTokens > 0 ? r.PromptTokens : r.EstimatedInputTokens,
                r.CompletionTokens,
                r.Cost,
                r.LatencyMs,
                r.Success,
                r.IsAdopted,
                r.RoutingReason));
        }

        return new DagTraceTree
        {
            TraceId = traceId,
            ParallelGroupId = parallelGroupId,
            TotalCost = totalCost,
            TotalPromptTokens = totalPrompt,
            TotalCompletionTokens = totalCompletion,
            MaxLatencyMs = maxLatency,
            Nodes = nodes
        };
    }
}
