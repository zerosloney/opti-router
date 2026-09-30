using OptiRouter.Configuration;
using OptiRouter.Mcp;

namespace OptiRouter.Routing;

/// <summary>
/// MCP / Function Calling 工具感知与复杂度动态分级路由策略 (MCP Tool Complexity Routing Policy)。
/// 当请求携带外部 Tool / MCP 工具定义时：
/// 1. 若复杂度高（&gt;5 个工具、或深层对象嵌套/复杂 enum），提升调度至 Strong 顶级模型，杜绝小模型在复杂 Schema 下的幻觉与参数格式错误；
/// 2. 若复杂度低（1~2 个简单扁平参数工具），允许保留调度至 Cheap / Medium 高性价比模型，节约 Token 与调用开销。
/// </summary>
public sealed class McpToolRoutingPolicy : IRouterPolicy
{
    private readonly McpToolComplexityAnalyzer _analyzer;

    public PolicyGroup Group => PolicyGroup.Classify;

    public McpToolRoutingPolicy(McpToolComplexityAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new McpToolComplexityAnalyzer();
    }

    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (context.Options?.Routing == null || !context.Options.Routing.EnableMcpComplexityRouting)
        {
            return previous;
        }

        if (context.Request == null || previous.Candidates.Count <= 1)
        {
            return previous;
        }

        var report = _analyzer.Analyze(context.Request);
        if (report.Level == McpComplexityLevel.None || report.ToolCount == 0)
        {
            return previous;
        }

        var candidates = previous.Candidates.ToList();

        // 复杂 Schema：优先调度 Strong 顶级模型，其次 Medium，最后 Cheap
        if (report.Level == McpComplexityLevel.High)
        {
            var prioritized = candidates
                .OrderBy(m => m.Tier switch
                {
                    ModelTier.Strong => 0,
                    ModelTier.Medium => 1,
                    _ => 2
                })
                .ToList();

            var updated = previous with { Candidates = prioritized };
            return updated.Append("mcp-tool-complexity",
                $"high_complexity: tools={report.ToolCount}, props={report.TotalProperties}, depth={report.MaxNestingDepth}, score={report.ComplexityScore}, prioritized Strong Tier");
        }

        // 中等复杂度：优先 Medium / Strong
        if (report.Level == McpComplexityLevel.Moderate)
        {
            var prioritized = candidates
                .OrderBy(m => m.Tier switch
                {
                    ModelTier.Medium => 0,
                    ModelTier.Strong => 1,
                    _ => 2
                })
                .ToList();

            var updated = previous with { Candidates = prioritized };
            return updated.Append("mcp-tool-complexity",
                $"moderate_complexity: tools={report.ToolCount}, props={report.TotalProperties}, depth={report.MaxNestingDepth}, score={report.ComplexityScore}, prioritized Medium/Strong Tier");
        }

        // 简单复杂度：优先 Cheap / Medium 降低成本
        if (report.Level == McpComplexityLevel.Simple)
        {
            var prioritized = candidates
                .OrderBy(m => m.Tier switch
                {
                    ModelTier.Cheap => 0,
                    ModelTier.Medium => 1,
                    _ => 2
                })
                .ToList();

            var updated = previous with { Candidates = prioritized };
            return updated.Append("mcp-tool-complexity",
                $"simple_complexity: tools={report.ToolCount}, props={report.TotalProperties}, score={report.ComplexityScore}, prioritized Cheap/Medium Tier");
        }

        return previous;
    }
}
