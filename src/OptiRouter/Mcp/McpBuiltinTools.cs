using System.Text.Json;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using OptiRouter.Health;

namespace OptiRouter.Mcp;

/// <summary>
/// OptiRouter 内置 MCP 工具集（由 <see cref="McpServerHost"/> 暴露给外部 MCP 客户端）。
///
/// 这些工具让外部 agent（Claude Code、Cline、Cursor 等）可以在对话中直接查询
/// OptiRouter 的路由状态、预算余额、模型健康等信息，无需访问 Dashboard。
/// </summary>
public sealed class OptiRouterMcpTools : McpServerToolProvider
{
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly CostLedger _ledger;
    private readonly ModelHealthTracker _healthTracker;
    private readonly McpToolRegistry _registry;
    private readonly ITokenEstimator _tokenEstimator;

    public OptiRouterMcpTools(
        IOptionsMonitor<RouterOptions> options,
        CostLedger ledger,
        ModelHealthTracker healthTracker,
        McpToolRegistry registry,
        ITokenEstimator tokenEstimator)
    {
        _options = options;
        _ledger = ledger;
        _healthTracker = healthTracker;
        _registry = registry;
        _tokenEstimator = tokenEstimator;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<McpServerTool>
        {
            new(
                "router_status",
                "Get OptiRouter service status: configured models count, routing enabled, total daily spend.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),

            new(
                "budget_status",
                "Get current budget status: daily spend, remaining, session spend.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),

            new(
                "model_health",
                "Get model health status: which models are circuit-broken, degraded, or healthy.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),

            new(
                "list_models",
                "List all configured models with their tier, pricing, and enabled status.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["tier"] = new { type = "string", description = "Filter by tier (Strong/Medium/Cheap)" }
                    }
                })),

            new(
                "routing_decision",
                "Simulate a routing decision for a given prompt. Returns which model would be selected and why.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["prompt"] = new { type = "string", description = "The user prompt to route" },
                        ["require_vision"] = new { type = "boolean", description = "Whether vision capability is required" },
                        ["require_tools"] = new { type = "boolean", description = "Whether tool-use capability is required" }
                    },
                    required = new[] { "prompt" }
                })),

            new(
                "estimate_tokens",
                "Estimate token count for a given text using the configured token estimator.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string", description = "Text to estimate tokens for" }
                    },
                    required = new[] { "text" }
                })),

            new(
                "get_metrics",
                "Get Prometheus-compatible metrics snapshot: request counts, costs, latencies.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),

            new(
                "mcp_tools_status",
                "Get status of registered MCP tools: call counts, failure rates, latency stats.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
        };

        return Task.FromResult<IReadOnlyList<McpServerTool>>(tools);
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? Content, string? Error)> ExecuteToolAsync(
        string toolName,
        JsonElement? arguments,
        CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                "router_status" => await GetRouterStatusAsync(ct),
                "budget_status" => GetBudgetStatus(),
                "model_health" => await GetModelHealthAsync(ct),
                "list_models" => ListModels(arguments),
                "routing_decision" => await SimulateRoutingAsync(arguments, ct),
                "estimate_tokens" => EstimateTokens(arguments),
                "get_metrics" => GetMetrics(),
                "mcp_tools_status" => GetMcpToolsStatus(),
                _ => (false, null, $"Unknown tool: {toolName}")
            };
        }
        catch (Exception ex)
        {
            return (false, null, $"Tool execution failed: {ex.Message}");
        }
    }

    private Task<(bool, string?, string?)> GetRouterStatusAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var result = new
        {
            service = "OptiRouter",
            status = "healthy",
            models_configured = opts.Models.Count,
            models_enabled = opts.Models.Count(m => m.Enabled),
            routing_auto_enabled = true,
            daily_budget_usd = opts.Budget.DailyBudgetUsd,
            tier_distribution = new
            {
                strong = opts.Models.Count(m => m.Tier == ModelTier.Strong),
                medium = opts.Models.Count(m => m.Tier == ModelTier.Medium),
                cheap = opts.Models.Count(m => m.Tier == ModelTier.Cheap)
            }
        };
        return Task.FromResult<(bool, string?, string?)>((true, JsonSerializer.Serialize(result), null));
    }

    private (bool, string?, string?) GetBudgetStatus()
    {
        var dailySpend = _ledger.GetDailySpend();
        var opts = _options.CurrentValue;
        var remaining = Math.Max(0, (double)(opts.Budget.DailyBudgetUsd - dailySpend));
        var result = new
        {
            daily_budget_usd = opts.Budget.DailyBudgetUsd,
            daily_spend_usd = dailySpend,
            remaining_usd = remaining,
            utilization_pct = opts.Budget.DailyBudgetUsd > 0
                ? Math.Round((double)dailySpend / (double)opts.Budget.DailyBudgetUsd * 100, 2)
                : 0.0
        };
        return (true, JsonSerializer.Serialize(result), null);
    }

    private async Task<(bool, string?, string?)> GetModelHealthAsync(CancellationToken ct)
    {
        var models = _options.CurrentValue.Models;
        var health = new List<object>();

        foreach (var model in models.Take(50)) // 上限 50 个
        {
            var state = _healthTracker.GetState(model.Name);
            health.Add(new
            {
                name = model.Name,
                tier = model.Tier.ToString(),
                enabled = model.Enabled,
                circuit_state = state.ToString()
            });
        }

        await Task.CompletedTask;
        return (true, JsonSerializer.Serialize(new { models = health }), null);
    }

    private (bool, string?, string?) ListModels(JsonElement? arguments)
    {
        var models = _options.CurrentValue.Models;
        string? tierFilter = null;

        if (arguments.HasValue && arguments.Value.TryGetProperty("tier", out var tierEl))
            tierFilter = tierEl.GetString();

        var filtered = string.IsNullOrWhiteSpace(tierFilter)
            ? models
            : models.Where(m => m.Tier.ToString().Equals(tierFilter, StringComparison.OrdinalIgnoreCase));

        var result = filtered.Select(m => new
        {
            name = m.Name,
            tier = m.Tier.ToString(),
            upstream_id = m.UpstreamModelId,
            base_url = m.BaseUrl,
            provider = m.Provider,
            max_context_tokens = m.MaxContextTokens,
            input_price_per_million = m.InputPricePerMillion,
            output_price_per_million = m.OutputPricePerMillion,
            enabled = m.Enabled,
            tags = m.Tags?.ToList() ?? new List<string>()
        });

        return (true, JsonSerializer.Serialize(new { models = result }), null);
    }

    private async Task<(bool, string?, string?)> SimulateRoutingAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || !arguments.Value.TryGetProperty("prompt", out var promptEl))
            return (false, null, "Missing required parameter: prompt");

        string prompt = promptEl.GetString() ?? "";
        bool requireVision = arguments.Value.TryGetProperty("require_vision", out var vEl) && vEl.GetBoolean();
        bool requireTools = arguments.Value.TryGetProperty("require_tools", out var tEl) && tEl.GetBoolean();

        await Task.CompletedTask;

        // 简化模拟：能力优先（Strong > Medium > Cheap）+ capability 匹配，
        // 非真实 RouterEngine 决策（分类器/预算/熔断不参与）。
        var opts = _options.CurrentValue;
        var candidates = opts.Models.Where(m => m.Enabled).OrderBy(m => m.Tier).ToList();

        if (requireVision)
            candidates = candidates.Where(m => m.Tags?.Contains("vision") == true).ToList();
        if (requireTools)
            candidates = candidates.Where(m => m.Tags?.Contains("tool-use") == true).ToList();

        var selected = candidates.FirstOrDefault();

        var result = new
        {
            prompt_length = prompt.Length,
            would_route_to = selected?.Name ?? "(none available)",
            tier = selected?.Tier.ToString() ?? null,
            reason = selected is null ? "no matching model found"
                : requireVision || requireTools
                    ? $"capability filter (vision={requireVision}, tools={requireTools}); capability-priority pick: {selected.Tier}"
                    : $"capability-priority pick (strongest enabled tier): {selected.Tier}",
            candidates_checked = opts.Models.Count(m => m.Enabled),
            matched_count = candidates.Count
        };

        return (true, JsonSerializer.Serialize(result), null);
    }

    private (bool, string?, string?) EstimateTokens(JsonElement? arguments)
    {
        if (!arguments.HasValue || !arguments.Value.TryGetProperty("text", out var textEl))
            return (false, null, "Missing required parameter: text");

        string text = textEl.GetString() ?? "";
        // 复用校准过的估算器（与代理路由同源），按单条 user 消息包装
        int estimate = _tokenEstimator.Estimate(new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", text) }
        });

        return (true, JsonSerializer.Serialize(new { text_length = text.Length, estimated_tokens = estimate }), null);
    }

    private (bool, string?, string?) GetMetrics()
    {
        // Prometheus metrics 端点由 prometheus-net 托管，这里只返回摘要
        var opts = _options.CurrentValue;
        var dailySpend = _ledger.GetDailySpend();

        var result = new
        {
            daily_cost_usd = dailySpend,
            budget_limit_usd = opts.Budget.DailyBudgetUsd,
            models_total = opts.Models.Count,
            models_enabled = opts.Models.Count(m => m.Enabled),
            budget_utilization_pct = opts.Budget.DailyBudgetUsd > 0
                ? Math.Round((double)dailySpend / (double)opts.Budget.DailyBudgetUsd * 100, 2)
                : 0.0,
            note = "Full Prometheus metrics available at /metrics"
        };
        return (true, JsonSerializer.Serialize(result), null);
    }

    private (bool, string?, string?) GetMcpToolsStatus()
    {
        var tools = _registry.GetAllTools();
        var status = tools.Select(t =>
        {
            var health = _registry.GetToolHealth(t.Name);
            return new
            {
                name = t.Name,
                server = t.ServerName,
                description = t.Description,
                total_calls = health.TotalCalls,
                failed_calls = health.FailedCalls,
                failure_rate_pct = Math.Round(health.FailureRate * 100, 2),
                avg_latency_ms = Math.Round(health.AverageLatencyMs, 1),
                is_degraded = health.IsDegraded
            };
        });

        return (true, JsonSerializer.Serialize(new
        {
            tools_count = tools.Count,
            tools = status
        }), null);
    }
}
