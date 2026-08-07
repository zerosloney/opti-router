using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 提供可视化配置和健康状态监控 Dashboard 页面及 API 接口。
/// </summary>
public static class DashboardHandler
{
    /// <summary>
    /// 注册监控 Dashboard 的 HTML 页面路由及监控 JSON 数据 API 路由。
    /// </summary>
    /// <param name="endpoints">路由构建器。</param>
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 1. Dashboard UI Main View
        endpoints.MapGet("/dashboard", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetHtmlContent()).ConfigureAwait(false);
        });

        // 2. Dashboard Live Metrics API (cached 1s)
        endpoints.MapGet("/api/dashboard/metrics", (
            CostLedger ledger,
            ModelHealthTracker tracker,
            IRequestAuditStore auditStore,
            AlertEngine alertEngine,
            ILatencyStatsProvider latencyStats,
            IOptions<RouterOptions> options,
            IMemoryCache cache) =>
        {
            return cache.GetOrCreate("dashboard:metrics", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                return ComputeMetrics(ledger, tracker, auditStore, alertEngine, latencyStats, options.Value);
            });
        });

        // 3. Spend Trends API (cached 5s)
        endpoints.MapGet("/api/dashboard/trends", (ICostLedgerStore store, IMemoryCache cache, int days) =>
        {
            if (days <= 0) days = 7;
            if (days > 90) days = 90;

            return cache.GetOrCreate($"dashboard:trends:{days}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
                return Results.Json(store.GetDailyHistory(days));
            });
        });

        // 4. Request Audit Log API (no cache - user actively queries)
        endpoints.MapGet("/api/dashboard/requests", (IRequestAuditStore auditStore, int limit = 50, int offset = 0, string? model = null, DateTime? from = null, DateTime? to = null) =>
        {
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            if (model is { Length: > 0 })
            {
                var items = auditStore.GetByModel(model, limit);
                return Results.Json(new { items, totalCount = items.Count });
            }

            DateTime fromTime = from ?? DateTime.UtcNow.AddHours(-1);
            DateTime toTime = to ?? DateTime.UtcNow;
            var (pageItems, totalCount) = auditStore.GetByTimeRange(fromTime, toTime, limit, offset);
            return Results.Json(new { items = pageItems, totalCount });
        });

        // 模型配置 CRUD 已迁移到 ModelsConfigHandler (/api/models/*)，与本监控页职责分离。
    }

    private static object ComputeMetrics(CostLedger ledger, ModelHealthTracker tracker, IRequestAuditStore auditStore, AlertEngine alertEngine, ILatencyStatsProvider latencyStats, RouterOptions options)
    {
        var circuitSnapshot = tracker.GetCircuitsSnapshot();
        var spend = ledger.GetSpend();
        var alerts = alertEngine.Check();

        // Compute QPS and aggregate stats from recent audit records.
        var recent = auditStore.GetRecent(500);
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
        int recentCount = recent.Count(r => r.Timestamp >= cutoff);
        double qps = recentCount / 60.0;

        int totalRequests = recent.Count;
        int totalTokens = recent.Sum(r => r.PromptTokens + r.CompletionTokens);
        double avgLatencyMs = totalRequests > 0 ? recent.Average(r => r.LatencyMs) : 0;

        var modelsList = options.Models.Select(m =>
        {
            // 延迟统计：后台聚合的内存快照，冷启动/低流量时为 null。
            var latencyStat = latencyStats.GetStats(m.Name);
            return new
            {
                m.Name,
                m.BaseUrl,
                m.Tier,
                m.InputPricePerMillion,
                m.OutputPricePerMillion,
                m.MaxContextTokens,
                m.Enabled,
                m.Tags,
                CircuitState = circuitSnapshot.TryGetValue(m.Name, out var info) ? info.State.ToString() : "Closed",
                FailureCount = circuitSnapshot.TryGetValue(m.Name, out var info2) ? info2.FailureCount : 0,
                ActiveProbes = circuitSnapshot.TryGetValue(m.Name, out var info3) ? info3.ActiveProbes : 0,
                // 延迟感知统计（无数据时 null/0，前端显示 '--'）
                AvgLatencyMs = latencyStat?.AverageLatencyMs,
                LatencySamples = latencyStat?.SampleCount ?? 0
            };
        }).ToList();

        return new
        {
            System = new
            {
                Time = DateTime.UtcNow,
                RoutingPolicy = options.Routing,
                Budget = new
                {
                    DailyBudgetUsd = options.Budget.DailyBudgetUsd,
                    options.Budget.UsePersistentStore,
                    DailySpend = spend.Daily,
                    TotalSpend = spend.Total
                },
                Qps = Math.Round(qps, 1),
                TotalRequests = totalRequests,
                TotalTokens = totalTokens,
                AvgLatencyMs = Math.Round(avgLatencyMs, 1),
                Alerts = alerts.Select(a => new { a.Id, a.Level, a.Category, a.Message, a.Timestamp })
            },
            Models = modelsList
        };
    }

    private static string GetHtmlContent()
    {
        return string.Join("\n", new[]
        {
            @"<!DOCTYPE html>",
            @"<html lang=""zh"">",
            @"<head>",
            @"    <meta charset=""UTF-8"">",
            @"    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">",
            @"    <title>OptiRouter - AI 网关管理面板</title>",
            @"    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap"" rel=""stylesheet"">",
            @"    <style>",
            @"        :root {",
            @"            --bg-base: #090d16;",
            @"            --bg-surface: rgba(16, 22, 35, 0.65);",
            @"            --bg-card: rgba(22, 30, 49, 0.8);",
            @"            --text-primary: #f3f4f6;",
            @"            --text-secondary: #9ca3af;",
            @"            --primary: #6366f1;",
            @"            --primary-glow: rgba(99, 102, 241, 0.15);",
            @"            --success: #10b981;",
            @"            --success-glow: rgba(16, 185, 129, 0.15);",
            @"            --warning: #f59e0b;",
            @"            --warning-glow: rgba(245, 158, 11, 0.15);",
            @"            --danger: #ef4444;",
            @"            --danger-glow: rgba(239, 68, 68, 0.15);",
            @"            --border: rgba(255, 255, 255, 0.06);",
            @"            --border-hover: rgba(255, 255, 255, 0.12);",
            @"        }",
            @"        * { box-sizing: border-box; margin: 0; padding: 0; }",
            @"        body {",
            @"            font-family: 'Outfit', -apple-system, BlinkMacSystemFont, sans-serif;",
            @"            background-color: var(--bg-base);",
            @"            color: var(--text-primary);",
            @"            min-height: 100vh; overflow-x: hidden;",
            @"            background-image:",
            @"                radial-gradient(circle at 10% 20%, rgba(99, 102, 241, 0.05) 0%, transparent 40%),",
            @"                radial-gradient(circle at 90% 80%, rgba(16, 185, 129, 0.03) 0%, transparent 40%);",
            @"        }",
            @"        header {",
            @"            display: flex; justify-content: space-between; align-items: center;",
            @"            padding: 1.5rem 2rem; border-bottom: 1px solid var(--border);",
            @"            backdrop-filter: blur(12px); position: sticky; top: 0; z-index: 100;",
            @"            background: rgba(9, 13, 22, 0.8);",
            @"        }",
            @"        .logo-area { display: flex; align-items: center; gap: 0.75rem; }",
            @"        .logo-icon {",
            @"            width: 2.2rem; height: 2.2rem;",
            @"            background: linear-gradient(135deg, var(--primary), #8b5cf6);",
            @"            border-radius: 0.5rem; display: flex; align-items: center; justify-content: center;",
            @"            box-shadow: 0 0 15px rgba(99, 102, 241, 0.4);",
            @"            font-weight: 700; font-size: 1.2rem;",
            @"        }",
            @"        .logo-title {",
            @"            font-size: 1.4rem; font-weight: 700; letter-spacing: -0.025em;",
            @"            background: linear-gradient(to right, #ffffff, #c7d2fe);",
            @"            -webkit-background-clip: text; -webkit-text-fill-color: transparent;",
            @"        }",
            @"        .system-time {",
            @"            font-family: 'JetBrains Mono', monospace; font-size: 0.85rem;",
            @"            color: var(--text-secondary);",
            @"            background: rgba(255, 255, 255, 0.03); padding: 0.4rem 0.8rem;",
            @"            border-radius: 0.375rem; border: 1px solid var(--border);",
            @"        }",
            @"        main {",
            @"            max-width: 1400px; margin: 0 auto; padding: 2rem;",
            @"            display: grid; grid-template-columns: 1fr; gap: 2rem;",
            @"        }",
            @"        @media (min-width: 1024px) { main { grid-template-columns: 280px 1fr; } }",
            @"        .sidebar { display: flex; flex-direction: column; gap: 1.5rem; }",
            @"        .glass-card {",
            @"            background: var(--bg-card); border: 1px solid var(--border);",
            @"            border-radius: 0.75rem; padding: 1.5rem;",
            @"            backdrop-filter: blur(16px);",
            @"            box-shadow: 0 8px 32px 0 rgba(0, 0, 0, 0.3);",
            @"            transition: border-color 0.3s, transform 0.3s;",
            @"        }",
            @"        .glass-card:hover { border-color: var(--border-hover); }",
            @"        .sidebar-title {",
            @"            font-size: 0.8rem; text-transform: uppercase;",
            @"            letter-spacing: 0.05em; color: var(--text-secondary);",
            @"            margin-bottom: 1rem; font-weight: 600;",
            @"        }",
            @"        .stat-value {",
            @"            font-size: 2rem; font-weight: 700;",
            @"            font-family: 'JetBrains Mono', monospace;",
            @"            margin: 0.25rem 0; color: #fff;",
            @"        }",
            @"        .budget-bar {",
            @"            width: 100%; height: 6px;",
            @"            background: rgba(255, 255, 255, 0.05);",
            @"            border-radius: 3px; margin-top: 0.75rem; overflow: hidden;",
            @"        }",
            @"        .budget-progress {",
            @"            height: 100%;",
            @"            background: linear-gradient(to right, var(--primary), #8b5cf6);",
            @"            width: 0%; transition: width 0.8s cubic-bezier(0.4, 0, 0.2, 1);",
            @"        }",
            @"        .budget-text {",
            @"            display: flex; justify-content: space-between;",
            @"            font-size: 0.8rem; color: var(--text-secondary); margin-top: 0.5rem;",
            @"        }",
            @"        .models-container {",
            @"            display: grid; grid-template-columns: 1fr; gap: 1.5rem;",
            @"        }",
            @"        @media (min-width: 768px) { .models-container { grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); } }",
            @"        .model-card { display: flex; flex-direction: column; justify-content: space-between; position: relative; overflow: hidden; }",
            @"        .model-card::after { content: ''; position: absolute; top: 0; left: 0; width: 4px; height: 100%; background: var(--success); }",
            @"        .model-card.state-closed::after { background: var(--success); }",
            @"        .model-card.state-open::after { background: var(--danger); }",
            @"        .model-card.state-halfopen::after { background: var(--warning); }",
            @"        .card-header-row { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1rem; }",
            @"        .model-name { font-size: 1.25rem; font-weight: 600; letter-spacing: -0.01em; }",
            @"        .status-badge {",
            @"            font-size: 0.75rem; padding: 0.25rem 0.6rem; border-radius: 2rem; font-weight: 600;",
            @"            text-transform: uppercase; letter-spacing: 0.025em;",
            @"        }",
            @"        .status-badge.closed { background-color: var(--success-glow); color: var(--success); border: 1px solid rgba(16, 185, 129, 0.2); }",
            @"        .status-badge.open { background-color: var(--danger-glow); color: var(--danger); border: 1px solid rgba(239, 68, 68, 0.2); }",
            @"        .status-badge.halfopen { background-color: var(--warning-glow); color: var(--warning); border: 1px solid rgba(245, 158, 11, 0.2); }",
            @"        .info-grid {",
            @"            display: grid; grid-template-columns: repeat(2, 1fr); gap: 0.75rem; font-size: 0.85rem;",
            @"            border-bottom: 1px solid var(--border); padding-bottom: 1rem; margin-bottom: 1rem;",
            @"        }",
            @"        .info-label { color: var(--text-secondary); }",
            @"        .info-val { font-family: 'JetBrains Mono', monospace; font-weight: 500; color: #fff; text-align: right; }",
            @"        .metrics-row { display: flex; justify-content: space-between; align-items: center; font-size: 0.85rem; }",
            @"        .metric-item { display: flex; flex-direction: column; gap: 0.25rem; }",
            @"        .metric-lbl { font-size: 0.75rem; color: var(--text-secondary); text-transform: uppercase; font-weight: 500; }",
            @"        .metric-val { font-family: 'JetBrains Mono', monospace; font-size: 1rem; font-weight: 600; }",
            @"        .indicator { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }",
            @"        .pulse-active { animation: pulse 2s infinite; }",
            @"        @keyframes pulse { 0% { transform: scale(0.9); opacity: 0.6; } 50% { transform: scale(1.1); opacity: 1; } 100% { transform: scale(0.9); opacity: 0.6; } }",
            @"        .refresh-btn {",
            @"            background: var(--primary); color: #fff; border: none;",
            @"            padding: 0.5rem 1rem; border-radius: 0.375rem;",
            @"            font-weight: 500; cursor: pointer; font-family: inherit; font-size: 0.85rem; transition: filter 0.2s;",
            @"        }",
            @"        .refresh-btn:hover { filter: brightness(1.15); }",
            @"        .banner-alert {",
            @"            background: var(--primary-glow); border: 1px solid rgba(99, 102, 241, 0.2);",
            @"            padding: 1rem; border-radius: 0.75rem; font-size: 0.9rem;",
            @"            margin-bottom: 1.5rem; display: flex; align-items: center; gap: 0.75rem;",
            @"        }",
            @"        .trend-section { margin-top: 0; }",
            @"        .trend-controls { display: flex; gap: 0.5rem; margin-bottom: 1rem; }",
            @"        .trend-controls button {",
            @"            background: var(--bg-surface); color: var(--text-secondary);",
            @"            border: 1px solid var(--border); padding: 0.35rem 0.75rem;",
            @"            border-radius: 0.375rem; cursor: pointer; font-family: inherit; font-size: 0.8rem; transition: all 0.2s;",
            @"        }",
            @"        .trend-controls button.active { background: var(--primary); color: #fff; border-color: var(--primary); }",
            @"        .trend-controls button:hover:not(.active) { border-color: var(--border-hover); color: var(--text-primary); }",
            @"        .chart-container { position: relative; width: 100%; height: 280px; }",
            @"        .chart-container canvas { width: 100%; height: 100%; }",
            @"        .stats-row { display: grid; grid-template-columns: repeat(2, 1fr); gap: 1rem; margin-bottom: 1.5rem; }",
            @"        @media (min-width: 768px) { .stats-row { grid-template-columns: repeat(4, 1fr); } }",
            @"        .stat-card {",
            @"            background: var(--bg-card); border: 1px solid var(--border);",
            @"            border-radius: 0.75rem; padding: 1.25rem; backdrop-filter: blur(16px);",
            @"        }",
            @"        .stat-card .stat-label { font-size: 0.75rem; text-transform: uppercase; color: var(--text-secondary); font-weight: 500; letter-spacing: 0.05em; }",
            @"        .stat-card .stat-value { font-size: 1.5rem; font-weight: 700; font-family: 'JetBrains Mono', monospace; margin-top: 0.25rem; }",
            @"        .log-section { margin-top: 2rem; }",
            @"        .log-controls { display: flex; gap: 0.75rem; margin-bottom: 1rem; flex-wrap: wrap; }",
            @"        .log-controls input, .log-controls select {",
            @"            background: var(--bg-surface); color: var(--text-primary);",
            @"            border: 1px solid var(--border); padding: 0.4rem 0.75rem;",
            @"            border-radius: 0.375rem; font-family: inherit; font-size: 0.85rem;",
            @"        }",
            @"        .log-controls input:focus, .log-controls select:focus { outline: none; border-color: var(--primary); }",
            @"        .log-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }",
            @"        .log-table th {",
            @"            text-align: left; padding: 0.6rem 0.75rem;",
            @"            color: var(--text-secondary); font-weight: 600;",
            @"            border-bottom: 1px solid var(--border);",
            @"            font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em;",
            @"        }",
            @"        .log-table td { padding: 0.6rem 0.75rem; border-bottom: 1px solid var(--border); font-family: 'JetBrains Mono', monospace; font-size: 0.8rem; }",
            @"        .log-table tr:hover td { background: rgba(255, 255, 255, 0.02); }",
            @"        .log-table .success { color: var(--success); }",
            @"        .log-table .failure { color: var(--danger); }",
            @"        .log-pagination { display: flex; justify-content: space-between; align-items: center; margin-top: 1rem; font-size: 0.85rem; color: var(--text-secondary); }",
            @"        .log-pagination button {",
            @"            background: var(--bg-surface); color: var(--text-primary);",
            @"            border: 1px solid var(--border); padding: 0.35rem 0.75rem;",
            @"            border-radius: 0.375rem; cursor: pointer; font-family: inherit; font-size: 0.8rem;",
            @"        }",
            @"        .log-pagination button:disabled { opacity: 0.4; cursor: not-allowed; }",
            @"        .config-section { margin-top: 2rem; }",
            @"    </style>",
            @"</head>",
            @"<body>",
            @"    <header>",
            @"        <div class=""logo-area"">",
            @"            <div class=""logo-icon"">&#937;</div>",
            @"            <div class=""logo-title"">OptiRouter</div>",
            @"        </div>",
            @"        <div style=""display: flex; align-items: center; gap: 1rem;"">",
            @"            <a href=""/models"" style=""color: var(--text-secondary); text-decoration: none; font-size: 0.9rem; padding: 0.4rem 0.8rem; border-radius: 0.375rem; border: 1px solid var(--border);"" onmouseover=""this.style.color='var(--text-primary)'"" onmouseout=""this.style.color='var(--text-secondary)'"">模型配置</a>",
            @"            <button class=""refresh-btn"" onclick=""fetchMetrics()"">刷新</button>",
            @"            <div class=""system-time"" id=""utc-time"">UTC: --:--:--</div>",
            @"        </div>",
            @"    </header>",
            @"    <main>",
            @"        <div class=""sidebar"">",
            @"            <div class=""glass-card"">",
            @"                <div class=""sidebar-title"">日预算消耗</div>",
            @"                <div class=""stat-value"" id=""daily-spend"">$0.000000</div>",
            @"                <div class=""budget-bar""><div class=""budget-progress"" id=""budget-bar-fill""></div></div>",
            @"                <div class=""budget-text"">",
            @"                    <span id=""budget-percent"">0% 已用</span>",
            @"                    <span id=""budget-limit"">上限 $0.00</span>",
            @"                </div>",
            @"            </div>",
            @"            <div class=""glass-card"">",
            @"                <div class=""sidebar-title"">累计消费</div>",
            @"                <div class=""stat-value"" id=""total-spend"" style=""color: #38bdf8;"">$0.000000</div>",
            @"                <div style=""font-size: 0.8rem; color: var(--text-secondary); margin-top: 0.5rem;"">服务启动以来的合计</div>",
            @"            </div>",
            @"            <div class=""glass-card"">",
            @"                <div class=""sidebar-title"">请求统计</div>",
            @"                <div style=""display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.85rem;"">",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">QPS（1分钟）</span>",
            @"                        <span id=""stat-qps"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">总请求数</span>",
            @"                        <span id=""stat-requests"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">总 Token 数</span>",
            @"                        <span id=""stat-tokens"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">平均延迟</span>",
            @"                        <span id=""stat-latency"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                </div>",
            @"            </div>",
            @"            <div class=""glass-card"">",
            @"                <div class=""sidebar-title"">路由引擎</div>",
            @"                <div style=""display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.85rem;"">",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">故障转移</span>",
            @"                        <span id=""engine-failover"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">预算防护</span>",
            @"                        <span id=""engine-budget"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                    <div style=""display: flex; justify-content: space-between;"">",
            @"                        <span class=""info-label"">分类器</span>",
            @"                        <span id=""engine-classifier"" class=""info-val"">--</span>",
            @"                    </div>",
            @"                </div>",
            @"            </div>",
            @"        </div>",
            @"        <div style=""display: flex; flex-direction: column; gap: 1.5rem;"">",
            @"            <div class=""banner-alert"">",
            @"                <span class=""indicator pulse-active"" style=""background: var(--primary);""></span>",
            @"                <span>已连接 OptiRouter AI 网关，自动刷新间隔 2 秒。</span>",
            @"            </div>",
            @"            <div class=""glass-card trend-section"">",
            @"                <div class=""sidebar-title"">费用趋势</div>",
            @"                <div class=""trend-controls"">",
            @"                    <button class=""active"" onclick=""setTrendDays(7, this)"">近7天</button>",
            @"                    <button onclick=""setTrendDays(30, this)"">近30天</button>",
            @"                </div>",
            @"                <div class=""chart-container""><canvas id=""trend-chart""></canvas></div>",
            @"            </div>",
            @"            <div class=""stats-row"">",
            @"                <div class=""stat-card""><div class=""stat-label"">QPS（1分钟）</div><div class=""stat-value"" id=""card-qps"">--</div></div>",
            @"                <div class=""stat-card""><div class=""stat-label"">总请求数</div><div class=""stat-value"" id=""card-requests"">--</div></div>",
            @"                <div class=""stat-card""><div class=""stat-label"">总 Token 数</div><div class=""stat-value"" id=""card-tokens"">--</div></div>",
            @"                <div class=""stat-card""><div class=""stat-label"">平均延迟</div><div class=""stat-value"" id=""card-latency"">--</div></div>",
            @"            </div>",
            @"            <div class=""models-container"" id=""models-grid""></div>",
            @"            <div class=""glass-card log-section"">",
            @"                <div class=""sidebar-title"">请求审计日志</div>",
            @"                <div class=""log-controls"">",
            @"                    <input type=""text"" id=""log-filter-model"" placeholder=""按模型筛选..."" onkeyup=""if(event.key==='Enter')loadLogs()"">",
            @"                    <select id=""log-limit"" onchange=""loadLogs()"">",
            @"                        <option value=""50"">每页 50 条</option>",
            @"                        <option value=""100"">每页 100 条</option>",
            @"                    </select>",
            @"                    <button class=""refresh-btn"" onclick=""loadLogs()"">加载</button>",
            @"                </div>",
            @"                <div style=""overflow-x: auto;"">",
            @"                    <table class=""log-table"">",
            @"                        <thead><tr><th>时间</th><th>模型</th><th>Token</th><th>费用</th><th>延迟</th><th>状态</th><th>流式</th></tr></thead>",
            @"                        <tbody id=""log-body""></tbody>",
            @"                    </table>",
            @"                </div>",
            @"                <div class=""log-pagination"">",
            @"                    <span id=""log-info"">--</span>",
            @"                    <div>",
            @"                        <button id=""log-prev"" onclick=""logPage(-1)"">上一页</button>",
            @"                        <button id=""log-next"" onclick=""logPage(1)"">下一页</button>",
            @"                    </div>",
            @"                </div>",
            @"            </div>",
            @"            <div class=""glass-card config-section"">",
            @"                <div class=""sidebar-title"" style=""margin-bottom:0.5rem;"">模型配置</div>",
            @"                <div style=""font-size:0.85rem; color:var(--text-secondary);"">配置已移至独立的 <a href=""/models"" style=""color:var(--primary); text-decoration:none;"">模型配置页</a>。</div>",
            @"            </div>",
            @"        </div>",
            @"    </main>",
            @"    <script src=""/dashboard.js""></script>",
            @"</body>",
            @"</html>"
        });
    }
}