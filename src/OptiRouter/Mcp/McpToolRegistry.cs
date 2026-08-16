using System.Collections.Concurrent;
using System.Text.Json;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP 单个工具的健康与延迟观测统计。
/// </summary>
public sealed class McpToolHealthStats
{
    private long _totalCalls;
    private long _failedCalls;
    private long _totalLatencyMs;

    public long TotalCalls => Interlocked.Read(ref _totalCalls);
    public long FailedCalls => Interlocked.Read(ref _failedCalls);
    public double FailureRate => TotalCalls == 0 ? 0.0 : (double)FailedCalls / TotalCalls;
    public double AverageLatencyMs => TotalCalls == 0 ? 0.0 : (double)Interlocked.Read(ref _totalLatencyMs) / TotalCalls;
    public bool IsDegraded => TotalCalls >= 5 && FailureRate >= 0.5;

    public void Record(bool success, long latencyMs)
    {
        Interlocked.Increment(ref _totalCalls);
        if (!success)
        {
            Interlocked.Increment(ref _failedCalls);
        }
        Interlocked.Add(ref _totalLatencyMs, Math.Max(0, latencyMs));
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalCalls, 0);
        Interlocked.Exchange(ref _failedCalls, 0);
        Interlocked.Exchange(ref _totalLatencyMs, 0);
    }
}

/// <summary>
/// MCP 工具注册中心与健康熔断隔离器 (MCP Tool Registry and Gateway Isolation)。
/// 统一管理已注册的 MCP Server 端点与工具元数据，维护各 Tool 运行期的延迟与故障率统计，
/// 实现工具级调用超时控制与故障熔断隔离，防止下游缓慢的 MCP 工具拖垮整个 Agent 对话流水线。
/// </summary>
public sealed class McpToolRegistry
{
    private readonly ConcurrentDictionary<string, McpToolRegistration> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerRegistration> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpToolHealthStats> _toolHealth = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 注册一个 MCP 服务器配置。
    /// </summary>
    public void RegisterServer(McpServerRegistration server)
    {
        if (server == null || string.IsNullOrWhiteSpace(server.Name)) return;
        _servers[server.Name] = server;
    }

    /// <summary>
    /// 注册或更新一个 MCP 工具。
    /// </summary>
    public void RegisterTool(McpToolRegistration tool)
    {
        if (tool == null || string.IsNullOrWhiteSpace(tool.Name)) return;
        _tools[tool.Name] = tool;
    }

    /// <summary>
    /// 获取所有已注册的工具列表。
    /// </summary>
    public IReadOnlyList<McpToolRegistration> GetAllTools()
    {
        return _tools.Values.ToList();
    }

    /// <summary>
    /// 按名称查找工具定义。
    /// </summary>
    public McpToolRegistration? GetTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return _tools.TryGetValue(toolName, out var tool) ? tool : null;
    }

    /// <summary>
    /// 按名称查找 MCP 服务器配置。
    /// </summary>
    public McpServerRegistration? GetServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return null;
        return _servers.TryGetValue(serverName, out var server) ? server : null;
    }

    /// <summary>
    /// 获取指定工具的健康与延迟统计。
    /// </summary>
    public McpToolHealthStats GetToolHealth(string toolName)
    {
        return _toolHealth.GetOrAdd(toolName, _ => new McpToolHealthStats());
    }

    /// <summary>
    /// 记录单次工具调用的执行结果与延迟。
    /// </summary>
    public void RecordToolExecution(string toolName, bool success, long latencyMs)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        var stats = _toolHealth.GetOrAdd(toolName, _ => new McpToolHealthStats());
        stats.Record(success, latencyMs);
    }

    /// <summary>
    /// 导出供 OpenAI / Anthropic 兼容请求格式的 tools JSON 数组元素。
    /// </summary>
    public JsonElement ExportOpenAiTools()
    {
        var list = new List<object>();
        foreach (var tool in _tools.Values)
        {
            list.Add(new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema ?? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                }
            });
        }
        return JsonSerializer.SerializeToElement(list);
    }
}
