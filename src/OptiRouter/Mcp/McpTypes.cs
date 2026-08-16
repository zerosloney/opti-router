using System.Text.Json;
using System.Text.Json.Serialization;
using OptiRouter.Configuration;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP / Function Calling 工具参数复杂度级别。
/// </summary>
public enum McpComplexityLevel
{
    /// <summary>
    /// 无工具定义。
    /// </summary>
    None = 0,

    /// <summary>
    /// 简单（1~2 个简单扁平参数工具，Cheap 模型即可稳定调用）。
    /// </summary>
    Simple = 1,

    /// <summary>
    /// 中等（3~5 个工具，或含嵌套对象/数组，建议 Medium 级别模型）。
    /// </summary>
    Moderate = 2,

    /// <summary>
    /// 复杂（&gt;5 个工具，或深层嵌套/复杂 enum/严格结构约束，强烈推荐 Strong 顶级模型）。
    /// </summary>
    High = 3
}

/// <summary>
/// MCP 工具复杂度分析报告。
/// </summary>
/// <param name="ToolCount">请求中携带的工具总数。</param>
/// <param name="TotalProperties">所有工具 Schema 的属性总数。</param>
/// <param name="MaxNestingDepth">JSON Schema 的最大嵌套层级。</param>
/// <param name="ComplexityScore">规范化综合复杂度评分 [0.0, 10.0]。</param>
/// <param name="Level">复杂度级别分类。</param>
/// <param name="RecommendedMinTier">建议的最低模型阶梯。</param>
public sealed record McpToolComplexityReport(
    int ToolCount,
    int TotalProperties,
    int MaxNestingDepth,
    double ComplexityScore,
    McpComplexityLevel Level,
    ModelTier RecommendedMinTier);

/// <summary>
/// MCP 工具注册元数据。
/// </summary>
public sealed record McpToolRegistration
{
    /// <summary>
    /// 工具名称。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 所属 MCP Server 名称（如 "filesystem", "github", "postgres"）。
    /// </summary>
    public string ServerName { get; init; } = "default";

    /// <summary>
    /// 工具功能描述。
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// JSON Schema 参数定义。
    /// </summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>
    /// 超时时间（毫秒）。默认 10000ms。
    /// </summary>
    public int TimeoutMs { get; init; } = 10000;

    /// <summary>
    /// 标签集合。
    /// </summary>
    public IList<string> Tags { get; init; } = new List<string>();
}

/// <summary>
/// MCP 上游服务器注册配置。
/// </summary>
public sealed record McpServerRegistration
{
    /// <summary>
    /// MCP Server 唯一名称。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// SSE 或 HTTP API Base URL（例如 "http://localhost:3001"）。
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// 认证 Token 或 API Key。
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// 是否启用该 Server。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 默认调用超时（毫秒）。
    /// </summary>
    public int TimeoutMs { get; init; } = 15000;
}
