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
            IOptions<RouterOptions> options,
            IMemoryCache cache) =>
        {
            return cache.GetOrCreate("dashboard:metrics", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                return ComputeMetrics(ledger, tracker, auditStore, alertEngine, options.Value);
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
        endpoints.MapGet("/api/dashboard/requests", (IRequestAuditStore auditStore, int limit, int offset, string? model, DateTime? from, DateTime? to) =>
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

        // 5. Model Config: GET all
        endpoints.MapGet("/api/dashboard/models", (IOptions<RouterOptions> options) =>
        {
            var models = options.Value.Models.Select(m => new
            {
                m.Name,
                m.BaseUrl,
                m.Tier,
                m.MaxContextTokens,
                m.TimeoutSeconds,
                m.MaxRetries,
                m.Enabled,
                m.InputPricePerMillion,
                m.OutputPricePerMillion,
                m.Tags
            }).ToList();
            return Results.Json(models);
        });

        // 6. Model Config: PUT update single model
        endpoints.MapPut("/api/dashboard/models/{name}", (string name, IOptionsMonitor<RouterOptions> optionsMonitor, UpdateModelRequest req) =>
        {
            var current = optionsMonitor.CurrentValue;
            var model = current.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (model is null)
                return Results.NotFound(new { error = $"Model '{name}' not found" });

            // Connection fields (BaseUrl, ApiKey) cannot be changed via Dashboard
            // because they require HttpClient rebuild with complex lifecycle semantics.
            if (req.Tier is not null) model.Tier = req.Tier.Value;
            if (req.MaxContextTokens is > 0) model.MaxContextTokens = req.MaxContextTokens.Value;
            if (req.TimeoutSeconds is > 0) model.TimeoutSeconds = req.TimeoutSeconds.Value;
            if (req.MaxRetries is >= 0) model.MaxRetries = req.MaxRetries.Value;
            if (req.Enabled is not null) model.Enabled = req.Enabled.Value;
            if (req.InputPricePerMillion is >= 0) model.InputPricePerMillion = req.InputPricePerMillion.Value;
            if (req.OutputPricePerMillion is >= 0) model.OutputPricePerMillion = req.OutputPricePerMillion.Value;

            // IOptionsMonitor.OnChange fires and ModelClientProvider picks up connection-relevant changes.
            // Tier/price/enabled changes take effect immediately via CurrentValue reads.

            return Results.Ok(new { message = $"Model '{name}' updated", model = new { model.Name, model.Tier, model.MaxContextTokens, model.TimeoutSeconds, model.MaxRetries, model.Enabled, model.InputPricePerMillion, model.OutputPricePerMillion } });
        });
    }

    private record UpdateModelRequest(
        ModelTier? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion);

    private static object ComputeMetrics(CostLedger ledger, ModelHealthTracker tracker, IRequestAuditStore auditStore, AlertEngine alertEngine, RouterOptions options)
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

        var modelsList = options.Models.Select(m => new
        {
            m.Name,
            m.BaseUrl,
            m.Tier,
            m.InputPricePerMillion,
            m.OutputPricePerMillion,
            m.MaxContextTokens,
            m.Enabled,
            CircuitState = circuitSnapshot.TryGetValue(m.Name, out var info) ? info.State.ToString() : "Closed",
            FailureCount = circuitSnapshot.TryGetValue(m.Name, out var info2) ? info2.FailureCount : 0,
            ActiveProbes = circuitSnapshot.TryGetValue(m.Name, out var info3) ? info3.ActiveProbes : 0
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
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>OptiRouter - AI Gateway Dashboard</title>
    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap"" rel=""stylesheet"">
    <style>
        :root {
            --bg-base: #090d16;
            --bg-surface: rgba(16, 22, 35, 0.65);
            --bg-card: rgba(22, 30, 49, 0.8);
            --text-primary: #f3f4f6;
            --text-secondary: #9ca3af;
            --primary: #6366f1;
            --primary-glow: rgba(99, 102, 241, 0.15);
            --success: #10b981;
            --success-glow: rgba(16, 185, 129, 0.15);
            --warning: #f59e0b;
            --warning-glow: rgba(245, 158, 11, 0.15);
            --danger: #ef4444;
            --danger-glow: rgba(239, 68, 68, 0.15);
            --border: rgba(255, 255, 255, 0.06);
            --border-hover: rgba(255, 255, 255, 0.12);
        }

        * { box-sizing: border-box; margin: 0; padding: 0; }

        body {
            font-family: 'Outfit', -apple-system, BlinkMacSystemFont, sans-serif;
            background-color: var(--bg-base);
            color: var(--text-primary);
            min-height: 100vh;
            overflow-x: hidden;
            background-image:
                radial-gradient(circle at 10% 20%, rgba(99, 102, 241, 0.05) 0%, transparent 40%),
                radial-gradient(circle at 90% 80%, rgba(16, 185, 129, 0.03) 0%, transparent 40%);
        }

        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 1.5rem 2rem;
            border-bottom: 1px solid var(--border);
            backdrop-filter: blur(12px);
            position: sticky;
            top: 0;
            z-index: 100;
            background: rgba(9, 13, 22, 0.8);
        }

        .logo-area { display: flex; align-items: center; gap: 0.75rem; }

        .logo-icon {
            width: 2.2rem; height: 2.2rem;
            background: linear-gradient(135deg, var(--primary), #8b5cf6);
            border-radius: 0.5rem;
            display: flex; align-items: center; justify-content: center;
            box-shadow: 0 0 15px rgba(99, 102, 241, 0.4);
            font-weight: 700; font-size: 1.2rem;
        }

        .logo-title {
            font-size: 1.4rem; font-weight: 700; letter-spacing: -0.025em;
            background: linear-gradient(to right, #ffffff, #c7d2fe);
            -webkit-background-clip: text; -webkit-text-fill-color: transparent;
        }

        .system-time {
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.85rem; color: var(--text-secondary);
            background: rgba(255, 255, 255, 0.03);
            padding: 0.4rem 0.8rem; border-radius: 0.375rem;
            border: 1px solid var(--border);
        }

        main {
            max-width: 1400px; margin: 0 auto; padding: 2rem;
            display: grid; grid-template-columns: 1fr; gap: 2rem;
        }

        @media (min-width: 1024px) {
            main { grid-template-columns: 280px 1fr; }
        }

        .sidebar { display: flex; flex-direction: column; gap: 1.5rem; }

        .glass-card {
            background: var(--bg-card); border: 1px solid var(--border);
            border-radius: 0.75rem; padding: 1.5rem;
            backdrop-filter: blur(16px);
            box-shadow: 0 8px 32px 0 rgba(0, 0, 0, 0.3);
            transition: border-color 0.3s, transform 0.3s;
        }

        .glass-card:hover { border-color: var(--border-hover); }

        .sidebar-title {
            font-size: 0.8rem; text-transform: uppercase;
            letter-spacing: 0.05em; color: var(--text-secondary);
            margin-bottom: 1rem; font-weight: 600;
        }

        .stat-value {
            font-size: 2rem; font-weight: 700;
            font-family: 'JetBrains Mono', monospace;
            margin: 0.25rem 0; color: #fff;
        }

        .budget-bar {
            width: 100%; height: 6px;
            background: rgba(255, 255, 255, 0.05);
            border-radius: 3px; margin-top: 0.75rem; overflow: hidden;
        }

        .budget-progress {
            height: 100%;
            background: linear-gradient(to right, var(--primary), #8b5cf6);
            width: 0%; transition: width 0.8s cubic-bezier(0.4, 0, 0.2, 1);
        }

        .budget-text {
            display: flex; justify-content: space-between;
            font-size: 0.8rem; color: var(--text-secondary); margin-top: 0.5rem;
        }

        .models-container {
            display: grid; grid-template-columns: 1fr; gap: 1.5rem;
        }

        @media (min-width: 768px) {
            .models-container { grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); }
        }

        .model-card {
            display: flex; flex-direction: column;
            justify-content: space-between;
            position: relative; overflow: hidden;
        }

        .model-card::after {
            content: ''; position: absolute; top: 0; left: 0;
            width: 4px; height: 100%; background: var(--success);
        }

        .model-card.state-closed::after { background: var(--success); }
        .model-card.state-open::after { background: var(--danger); }
        .model-card.state-halfopen::after { background: var(--warning); }

        .card-header-row {
            display: flex; justify-content: space-between;
            align-items: flex-start; margin-bottom: 1rem;
        }

        .model-name { font-size: 1.25rem; font-weight: 600; letter-spacing: -0.01em; }

        .status-badge {
            font-size: 0.75rem; padding: 0.25rem 0.6rem;
            border-radius: 2rem; font-weight: 600;
            text-transform: uppercase; letter-spacing: 0.025em;
        }

        .status-badge.closed { background-color: var(--success-glow); color: var(--success); border: 1px solid rgba(16, 185, 129, 0.2); }
        .status-badge.open { background-color: var(--danger-glow); color: var(--danger); border: 1px solid rgba(239, 68, 68, 0.2); }
        .status-badge.halfopen { background-color: var(--warning-glow); color: var(--warning); border: 1px solid rgba(245, 158, 11, 0.2); }

        .info-grid {
            display: grid; grid-template-columns: repeat(2, 1fr);
            gap: 0.75rem; font-size: 0.85rem;
            border-bottom: 1px solid var(--border);
            padding-bottom: 1rem; margin-bottom: 1rem;
        }

        .info-label { color: var(--text-secondary); }

        .info-val {
            font-family: 'JetBrains Mono', monospace;
            font-weight: 500; color: #fff; text-align: right;
        }

        .metrics-row {
            display: flex; justify-content: space-between;
            align-items: center; font-size: 0.85rem;
        }

        .metric-item { display: flex; flex-direction: column; gap: 0.25rem; }

        .metric-lbl {
            font-size: 0.75rem; color: var(--text-secondary);
            text-transform: uppercase; font-weight: 500;
        }

        .metric-val {
            font-family: 'JetBrains Mono', monospace;
            font-size: 1rem; font-weight: 600;
        }

        .indicator {
            width: 8px; height: 8px; border-radius: 50%;
            display: inline-block;
        }

        .pulse-active { animation: pulse 2s infinite; }

        @keyframes pulse {
            0% { transform: scale(0.9); opacity: 0.6; }
            50% { transform: scale(1.1); opacity: 1; }
            100% { transform: scale(0.9); opacity: 0.6; }
        }

        .refresh-btn {
            background: var(--primary); color: #fff; border: none;
            padding: 0.5rem 1rem; border-radius: 0.375rem;
            font-weight: 500; cursor: pointer; font-family: inherit;
            font-size: 0.85rem; transition: filter 0.2s;
        }

        .refresh-btn:hover { filter: brightness(1.15); }

        .banner-alert {
            background: var(--primary-glow);
            border: 1px solid rgba(99, 102, 241, 0.2);
            padding: 1rem; border-radius: 0.75rem;
            font-size: 0.9rem; margin-bottom: 1.5rem;
            display: flex; align-items: center; gap: 0.75rem;
        }

        /* Alert banner (fixed top) */
        .alert-banner {
            display: none;
            padding: 0.75rem 2rem;
            font-size: 0.85rem;
            font-weight: 500;
            position: sticky; top: 0; z-index: 200;
        }

        .alert-banner.visible { display: flex; align-items: center; gap: 0.75rem; }

        .alert-banner.warning {
            background: var(--warning-glow);
            border-bottom: 1px solid rgba(245, 158, 11, 0.3);
            color: var(--warning);
        }

        .alert-banner.critical {
            background: var(--danger-glow);
            border-bottom: 1px solid rgba(239, 68, 68, 0.3);
            color: var(--danger);
        }

        .alert-banner .alert-close {
            margin-left: auto; cursor: pointer;
            background: none; border: none; color: inherit;
            font-size: 1.2rem; line-height: 1;
        }

        /* Trend chart section */
        .trend-section { margin-top: 0; }

        .trend-controls {
            display: flex; gap: 0.5rem; margin-bottom: 1rem;
        }

        .trend-controls button {
            background: var(--bg-surface); color: var(--text-secondary);
            border: 1px solid var(--border); padding: 0.35rem 0.75rem;
            border-radius: 0.375rem; cursor: pointer; font-family: inherit;
            font-size: 0.8rem; transition: all 0.2s;
        }

        .trend-controls button.active {
            background: var(--primary); color: #fff;
            border-color: var(--primary);
        }

        .trend-controls button:hover:not(.active) {
            border-color: var(--border-hover); color: var(--text-primary);
        }

        .chart-container {
            position: relative; width: 100%; height: 280px;
        }

        .chart-container canvas { width: 100%; height: 100%; }

        /* Stats row */
        .stats-row {
            display: grid; grid-template-columns: repeat(2, 1fr); gap: 1rem;
            margin-bottom: 1.5rem;
        }

        @media (min-width: 768px) {
            .stats-row { grid-template-columns: repeat(4, 1fr); }
        }

        .stat-card {
            background: var(--bg-card); border: 1px solid var(--border);
            border-radius: 0.75rem; padding: 1.25rem;
            backdrop-filter: blur(16px);
        }

        .stat-card .stat-label {
            font-size: 0.75rem; text-transform: uppercase;
            color: var(--text-secondary); font-weight: 500;
            letter-spacing: 0.05em;
        }

        .stat-card .stat-value {
            font-size: 1.5rem; font-weight: 700;
            font-family: 'JetBrains Mono', monospace;
            margin-top: 0.25rem;
        }

        /* Request log table */
        .log-section { margin-top: 2rem; }

        .log-controls {
            display: flex; gap: 0.75rem; margin-bottom: 1rem;
            flex-wrap: wrap;
        }

        .log-controls input, .log-controls select {
            background: var(--bg-surface); color: var(--text-primary);
            border: 1px solid var(--border); padding: 0.4rem 0.75rem;
            border-radius: 0.375rem; font-family: inherit; font-size: 0.85rem;
        }

        .log-controls input:focus, .log-controls select:focus {
            outline: none; border-color: var(--primary);
        }

        .log-table {
            width: 100%; border-collapse: collapse;
            font-size: 0.85rem;
        }

        .log-table th {
            text-align: left; padding: 0.6rem 0.75rem;
            color: var(--text-secondary); font-weight: 600;
            border-bottom: 1px solid var(--border);
            font-size: 0.75rem; text-transform: uppercase;
            letter-spacing: 0.05em;
        }

        .log-table td {
            padding: 0.6rem 0.75rem;
            border-bottom: 1px solid var(--border);
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.8rem;
        }

        .log-table tr:hover td { background: rgba(255, 255, 255, 0.02); }

        .log-table .success { color: var(--success); }
        .log-table .failure { color: var(--danger); }

        .log-pagination {
            display: flex; justify-content: space-between;
            align-items: center; margin-top: 1rem;
            font-size: 0.85rem; color: var(--text-secondary);
        }

        .log-pagination button {
            background: var(--bg-surface); color: var(--text-primary);
            border: 1px solid var(--border); padding: 0.35rem 0.75rem;
            border-radius: 0.375rem; cursor: pointer; font-family: inherit;
            font-size: 0.8rem;
        }

        .log-pagination button:disabled {
            opacity: 0.4; cursor: not-allowed;
        }

        /* Model config editor */
        .config-section { margin-top: 2rem; }

        .config-table {
            width: 100%; border-collapse: collapse;
            font-size: 0.85rem;
        }

        .config-table th {
            text-align: left; padding: 0.6rem 0.75rem;
            color: var(--text-secondary); font-weight: 600;
            border-bottom: 1px solid var(--border);
            font-size: 0.75rem; text-transform: uppercase;
            letter-spacing: 0.05em;
        }

        .config-table td {
            padding: 0.5rem 0.75rem;
            border-bottom: 1px solid var(--border);
        }

        .config-table input, .config-table select {
            background: rgba(255, 255, 255, 0.03);
            color: var(--text-primary); border: 1px solid var(--border);
            padding: 0.3rem 0.5rem; border-radius: 0.25rem;
            font-family: 'JetBrains Mono', monospace; font-size: 0.8rem;
            width: 100%;
        }

        .config-table input:focus, .config-table select:focus {
            outline: none; border-color: var(--primary);
        }

        .config-table .save-btn {
            background: var(--primary); color: #fff;
            border: none; padding: 0.35rem 0.75rem;
            border-radius: 0.375rem; cursor: pointer;
            font-family: inherit; font-size: 0.8rem;
        }

        .config-table .save-btn:hover { filter: brightness(1.15); }

        .config-table .save-btn:disabled { opacity: 0.4; cursor: not-allowed; }

        .config-toast {
            position: fixed; bottom: 2rem; right: 2rem;
            padding: 0.75rem 1.25rem; border-radius: 0.5rem;
            font-size: 0.85rem; font-weight: 500;
            z-index: 300; display: none;
        }

        .config-toast.success {
            display: block; background: var(--success-glow);
            border: 1px solid rgba(16, 185, 129, 0.3); color: var(--success);
        }

        .config-toast.error {
            display: block; background: var(--danger-glow);
            border: 1px solid rgba(239, 68, 68, 0.3); color: var(--danger);
        }
    </style>
</head>
<body>
    <div class=""alert-banner"" id=""alert-banner"">
        <span id=""alert-icon""></span>
        <span id=""alert-message""></span>
        <button class=""alert-close"" onclick=""dismissAlert()"">&times;</button>
    </div>

    <header>
        <div class=""logo-area"">
            <div class=""logo-icon"">Ω</div>
            <div class=""logo-title"">OptiRouter</div>
        </div>
        <div style=""display: flex; align-items: center; gap: 1rem;"">
            <button class=""refresh-btn"" onclick=""fetchMetrics()"">Refresh</button>
            <div class=""system-time"" id=""utc-time"">UTC: --:--:--</div>
        </div>
    </header>

    <main>
        <div class=""sidebar"">
            <!-- Daily Budget Tracker -->
            <div class=""glass-card"">
                <div class=""sidebar-title"">Daily Spend Budget</div>
                <div class=""stat-value"" id=""daily-spend"">$0.000000</div>
                <div class=""budget-bar"">
                    <div class=""budget-progress"" id=""budget-bar-fill""></div>
                </div>
                <div class=""budget-text"">
                    <span id=""budget-percent"">0% Used</span>
                    <span id=""budget-limit"">Max $0.00</span>
                </div>
            </div>

            <!-- Accumulated Spending -->
            <div class=""glass-card"">
                <div class=""sidebar-title"">Accumulated Total</div>
                <div class=""stat-value"" id=""total-spend"" style=""color: #38bdf8;"">$0.000000</div>
                <div style=""font-size: 0.8rem; color: var(--text-secondary); margin-top: 0.5rem;"">
                    Since server startup
                </div>
            </div>

            <!-- Stats Panel -->
            <div class=""glass-card"">
                <div class=""sidebar-title"">Request Stats</div>
                <div style=""display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.85rem;"">
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">QPS (1 min)</span>
                        <span id=""stat-qps"" class=""info-val"">--</span>
                    </div>
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Total Requests</span>
                        <span id=""stat-requests"" class=""info-val"">--</span>
                    </div>
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Total Tokens</span>
                        <span id=""stat-tokens"" class=""info-val"">--</span>
                    </div>
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Avg Latency</span>
                        <span id=""stat-latency"" class=""info-val"">--</span>
                    </div>
                </div>
            </div>

            <!-- Policy & Rules Status -->
            <div class=""glass-card"">
                <div class=""sidebar-title"">Routing Engine</div>
                <div style=""display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.85rem;"">
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Failover</span>
                        <span id=""engine-failover"" class=""info-val"">--</span>
                    </div>
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Budget Guard</span>
                        <span id=""engine-budget"" class=""info-val"">--</span>
                    </div>
                    <div style=""display: flex; justify-content: space-between;"">
                        <span class=""info-label"">Classifier</span>
                        <span id=""engine-classifier"" class=""info-val"">--</span>
                    </div>
                </div>
            </div>
        </div>

        <div style=""display: flex; flex-direction: column; gap: 1.5rem;"">
            <div class=""banner-alert"">
                <span class=""indicator pulse-active"" style=""background: var(--primary);""></span>
                <span>Active Routing Dashboard: Connected to Native AOT microsecond-optimized proxy routing engine. Auto-refreshing every 2s.</span>
            </div>

            <!-- Trend Chart -->
            <div class=""glass-card trend-section"">
                <div class=""sidebar-title"">Spend Trends</div>
                <div class=""trend-controls"">
                    <button class=""active"" onclick=""setTrendDays(7, this)"">7 Days</button>
                    <button onclick=""setTrendDays(30, this)"">30 Days</button>
                </div>
                <div class=""chart-container"">
                    <canvas id=""trend-chart""></canvas>
                </div>
            </div>

            <!-- Stats Row -->
            <div class=""stats-row"">
                <div class=""stat-card"">
                    <div class=""stat-label"">QPS (1 min)</div>
                    <div class=""stat-value"" id=""card-qps"">--</div>
                </div>
                <div class=""stat-card"">
                    <div class=""stat-label"">Total Requests</div>
                    <div class=""stat-value"" id=""card-requests"">--</div>
                </div>
                <div class=""stat-card"">
                    <div class=""stat-label"">Total Tokens</div>
                    <div class=""stat-value"" id=""card-tokens"">--</div>
                </div>
                <div class=""stat-card"">
                    <div class=""stat-label"">Avg Latency</div>
                    <div class=""stat-value"" id=""card-latency"">--</div>
                </div>
            </div>

            <!-- Models Grid -->
            <div class=""models-container"" id=""models-grid""></div>

            <!-- Request Log -->
            <div class=""glass-card log-section"">
                <div class=""sidebar-title"">Request Audit Log</div>
                <div class=""log-controls"">
                    <input type=""text"" id=""log-filter-model"" placeholder=""Filter by model..."" onkeyup=""if(event.key==='Enter')loadLogs()"">
                    <select id=""log-limit"" onchange=""loadLogs()"">
                        <option value=""50"">50 per page</option>
                        <option value=""100"">100 per page</option>
                    </select>
                    <button class=""refresh-btn"" onclick=""loadLogs()"">Load</button>
                </div>
                <div style=""overflow-x: auto;"">
                    <table class=""log-table"">
                        <thead>
                            <tr>
                                <th>Time</th>
                                <th>Model</th>
                                <th>Tokens</th>
                                <th>Cost</th>
                                <th>Latency</th>
                                <th>Status</th>
                                <th>Stream</th>
                            </tr>
                        </thead>
                        <tbody id=""log-body""></tbody>
                    </table>
                </div>
                <div class=""log-pagination"">
                    <span id=""log-info"">--</span>
                    <div>
                        <button id=""log-prev"" onclick=""logPage(-1)"">Prev</button>
                        <button id=""log-next"" onclick=""logPage(1)"">Next</button>
                    </div>
                </div>
            </div>

            <!-- Model Config Editor -->
            <div class=""glass-card config-section"">
                <div class=""sidebar-title"">Model Configuration</div>
                <div style=""overflow-x: auto;"">
                    <table class=""config-table"">
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>Tier</th>
                                <th>Max Context</th>
                                <th>Timeout (s)</th>
                                <th>Retries</th>
                                <th>Enabled</th>
                                <th>Input $/M</th>
                                <th>Output $/M</th>
                                <th>Action</th>
                            </tr>
                        </thead>
                        <tbody id=""config-body""></tbody>
                    </table>
                </div>
            </div>
        </div>
    </main>

    <div class=""config-toast"" id=""config-toast""></div>

    <script>
        let trendDays = 7;
        let logOffset = 0;
        let logLimit = 50;
        let logTotal = 0;
        let dismissedAlerts = new Set();
        let pendingAlerts = [];

        async function fetchMetrics() {
            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const url = key ? '/api/dashboard/metrics?key=' + encodeURIComponent(key) : '/api/dashboard/metrics';
                const opts = key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };
                const response = await fetch(url, opts);
                if (!response.ok) throw new Error('Failed to fetch metrics');
                const data = await response.json();
                renderDashboard(data);
            } catch (err) {
                console.error('Error loading dashboard metrics:', err);
            }
        }

        function renderDashboard(data) {
            const sys = data.system;

            // Time
            const sysTime = new Date(sys.time);
            document.getElementById('utc-time').textContent = 'UTC: ' + sysTime.toISOString().split('T')[1].substring(0, 8);

            // Budget
            const budget = sys.budget;
            const dailySpend = budget.dailySpend || 0;
            const limit = budget.dailyBudgetUsd || 10;
            const percent = limit > 0 ? (dailySpend / limit) * 100 : 0;

            document.getElementById('daily-spend').textContent = '$' + dailySpend.toFixed(6);
            document.getElementById('total-spend').textContent = '$' + (budget.totalSpend || 0).toFixed(6);
            document.getElementById('budget-bar-fill').style.width = Math.min(percent, 100) + '%';
            document.getElementById('budget-percent').textContent = percent.toFixed(2) + '% Used';
            document.getElementById('budget-limit').textContent = 'Max $' + limit.toFixed(2);

            // Stats
            document.getElementById('stat-qps').textContent = sys.qps;
            document.getElementById('stat-requests').textContent = sys.totalRequests;
            document.getElementById('stat-tokens').textContent = sys.totalTokens.toLocaleString();
            document.getElementById('stat-latency').textContent = sys.avgLatencyMs + ' ms';

            document.getElementById('card-qps').textContent = sys.qps;
            document.getElementById('card-requests').textContent = sys.totalRequests;
            document.getElementById('card-tokens').textContent = sys.totalTokens.toLocaleString();
            document.getElementById('card-latency').textContent = sys.avgLatencyMs + ' ms';

            // Routing Engine Info
            const policy = sys.routingPolicy;
            document.getElementById('engine-failover').textContent = policy.enableFailover ? 'Active' : 'Disabled';
            document.getElementById('engine-failover').style.color = policy.enableFailover ? 'var(--success)' : 'var(--text-secondary)';
            document.getElementById('engine-budget').textContent = policy.enableBudgetGuard ? 'Active' : 'Disabled';
            document.getElementById('engine-budget').style.color = policy.enableBudgetGuard ? 'var(--success)' : 'var(--text-secondary)';
            document.getElementById('engine-classifier').textContent = policy.enableRuleClassifier ? 'Active' : 'Disabled';
            document.getElementById('engine-classifier').style.color = policy.enableRuleClassifier ? 'var(--success)' : 'var(--text-secondary)';

            // Alerts
            renderAlerts(sys.alerts || []);

            // Models Grid
            renderModels(data.models);

            // Trend chart (async fetch)
            fetchTrends();

            // Logs
            loadLogs();
        }

        function renderAlerts(alerts) {
            pendingAlerts = alerts.filter(a => !dismissedAlerts.has(a.id));
            if (pendingAlerts.length === 0) {
                document.getElementById('alert-banner').classList.remove('visible', 'warning', 'critical');
                return;
            }

            const worst = pendingAlerts[0];
            const banner = document.getElementById('alert-banner');
            banner.classList.remove('warning', 'critical');
            banner.classList.add(worst.level, 'visible');

            document.getElementById('alert-icon').textContent = worst.level === 'critical' ? '🔴' : '🟡';
            document.getElementById('alert-message').textContent = pendingAlerts.map(a => a.message).join(' | ');
        }

        function dismissAlert() {
            pendingAlerts.forEach(a => dismissedAlerts.add(a.id));
            renderAlerts([]);
        }

        function renderModels(models) {
            const grid = document.getElementById('models-grid');
            grid.innerHTML = '';

            models.forEach(model => {
                const card = document.createElement('div');
                const stateClass = model.circuitState.toLowerCase();
                card.className = `glass-card model-card state-${stateClass}`;

                let stateBadgeColor = 'closed';
                if (stateClass === 'open') stateBadgeColor = 'open';
                if (stateClass === 'halfopen') stateBadgeColor = 'halfopen';

                card.innerHTML = `
                    <div class=""card-header-row"">
                        <div>
                            <div class=""model-name"">${model.name}</div>
                            <div style=""font-size: 0.75rem; color: var(--text-secondary); margin-top: 0.15rem;"">${model.tier} Tier</div>
                        </div>
                        <span class=""status-badge ${stateBadgeColor}"">${model.circuitState}</span>
                    </div>
                    <div class=""info-grid"">
                        <span class=""info-label"">Base Url</span>
                        <span class=""info-val"" style=""overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 180px;"">${model.baseUrl}</span>
                        <span class=""info-label"">Input Price / M</span>
                        <span class=""info-val"">$${model.inputPricePerMillion.toFixed(2)}</span>
                        <span class=""info-label"">Output Price / M</span>
                        <span class=""info-val"">$${model.outputPricePerMillion.toFixed(2)}</span>
                        <span class=""info-label"">Max Context</span>
                        <span class=""info-val"">${model.maxContextTokens.toLocaleString()}</span>
                    </div>
                    <div class=""metrics-row"">
                        <div class=""metric-item"">
                            <span class=""metric-lbl"">Failures</span>
                            <span class=""metric-val"" style=""color: ${model.failureCount > 0 ? 'var(--danger)' : 'var(--text-secondary)'}"">${model.failureCount}</span>
                        </div>
                        <div class=""metric-item"" style=""text-align: right;"">
                            <span class=""metric-lbl"">Active Probes</span>
                            <span class=""metric-val"" style=""color: ${model.activeProbes > 0 ? 'var(--warning)' : 'var(--text-secondary)'}"">${model.activeProbes}</span>
                        </div>
                    </div>
                `;
                grid.appendChild(card);
            });
        }

        async function fetchTrends() {
            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const base = key ? '/api/dashboard/trends?key=' + encodeURIComponent(key) : '/api/dashboard/trends';
                const opts = key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };
                const url = `${base}&days=${trendDays}`;
                const response = await fetch(url, opts);
                if (!response.ok) return;
                const data = await response.json();
                drawTrendChart(data);
            } catch (err) {
                console.error('Error loading trends:', err);
            }
        }

        function setTrendDays(days, btn) {
            trendDays = days;
            document.querySelectorAll('.trend-controls button').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            fetchTrends();
        }

        function drawTrendChart(data) {
            const canvas = document.getElementById('trend-chart');
            if (!canvas || !data || data.length === 0) return;

            const ctx = canvas.getContext('2d');
            const dpr = window.devicePixelRatio || 1;
            const rect = canvas.parentElement.getBoundingClientRect();
            canvas.width = rect.width * dpr;
            canvas.height = rect.height * dpr;
            ctx.scale(dpr, dpr);
            const w = rect.width;
            const h = rect.height;

            const padding = { top: 20, right: 20, bottom: 30, left: 60 };
            const chartW = w - padding.left - padding.right;
            const chartH = h - padding.top - padding.bottom;

            ctx.clearRect(0, 0, w, h);

            const amounts = data.map(d => d.amount);
            const maxVal = Math.max(...amounts, 0.01);
            const minVal = 0;

            // Grid lines
            ctx.strokeStyle = 'rgba(255,255,255,0.04)';
            ctx.lineWidth = 1;
            for (let i = 0; i <= 4; i++) {
                const y = padding.top + (chartH * i / 4);
                ctx.beginPath();
                ctx.moveTo(padding.left, y);
                ctx.lineTo(w - padding.right, y);
                ctx.stroke();

                ctx.fillStyle = '#9ca3af';
                ctx.font = '11px JetBrains Mono';
                ctx.textAlign = 'right';
                const val = maxVal - (maxVal * i / 4);
                ctx.fillText('$' + val.toFixed(4), padding.left - 8, y + 4);
            }

            // Data points
            const points = data.map((d, i) => ({
                x: padding.left + (chartW * i / Math.max(data.length - 1, 1)),
                y: padding.top + chartH - (chartH * (d.amount - minVal) / (maxVal - minVal))
            }));

            // Gradient fill
            const gradient = ctx.createLinearGradient(0, padding.top, 0, h - padding.bottom);
            gradient.addColorStop(0, 'rgba(99, 102, 241, 0.3)');
            gradient.addColorStop(1, 'rgba(99, 102, 241, 0.0)');

            ctx.beginPath();
            ctx.moveTo(points[0].x, h - padding.bottom);
            points.forEach(p => ctx.lineTo(p.x, p.y));
            ctx.lineTo(points[points.length - 1].x, h - padding.bottom);
            ctx.closePath();
            ctx.fillStyle = gradient;
            ctx.fill();

            // Line
            ctx.beginPath();
            ctx.strokeStyle = '#6366f1';
            ctx.lineWidth = 2;
            ctx.lineJoin = 'round';
            points.forEach((p, i) => {
                if (i === 0) ctx.moveTo(p.x, p.y);
                else ctx.lineTo(p.x, p.y);
            });
            ctx.stroke();

            // Points
            points.forEach(p => {
                ctx.beginPath();
                ctx.arc(p.x, p.y, 3, 0, Math.PI * 2);
                ctx.fillStyle = '#6366f1';
                ctx.fill();
            });

            // X-axis labels
            ctx.fillStyle = '#9ca3af';
            ctx.font = '10px JetBrains Mono';
            ctx.textAlign = 'center';
            data.forEach((d, i) => {
                const x = padding.left + (chartW * i / Math.max(data.length - 1, 1));
                const label = new Date(d.date + 'T00:00:00Z').toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
                ctx.fillText(label, x, h - padding.bottom + 16);
            });
        }

        async function loadLogs() {
            const model = document.getElementById('log-filter-model').value.trim();
            logLimit = parseInt(document.getElementById('log-limit').value) || 50;

            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const base = key ? '/api/dashboard/requests?key=' + encodeURIComponent(key) : '/api/dashboard/requests';
                const opts = key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };

                let url = `${base}&limit=${logLimit}&offset=${logOffset}`;
                if (model) url += `&model=${encodeURIComponent(model)}`;

                const response = await fetch(url, opts);
                if (!response.ok) return;
                const data = await response.json();
                logTotal = data.totalCount || 0;

                const tbody = document.getElementById('log-body');
                tbody.innerHTML = '';

                data.items.forEach(item => {
                    const tr = document.createElement('tr');
                    const time = new Date(item.timestamp).toLocaleTimeString();
                    const statusClass = item.success ? 'success' : 'failure';
                    const statusText = item.success ? 'OK' : 'FAIL';
                    tr.innerHTML = `
                        <td>${time}</td>
                        <td>${item.model}</td>
                        <td>${item.promptTokens + item.completionTokens}</td>
                        <td>$${item.cost.toFixed(6)}</td>
                        <td>${item.latencyMs}ms</td>
                        <td class=""${statusClass}"">${statusText}</td>
                        <td>${item.isStreaming ? 'Yes' : 'No'}</td>
                    `;
                    tbody.appendChild(tr);
                });

                document.getElementById('log-info').textContent =
                    `Showing ${data.items.length} of ${logTotal} requests`;
                document.getElementById('log-prev').disabled = logOffset === 0;
                document.getElementById('log-next').disabled = logOffset + logLimit >= logTotal;
            } catch (err) {
                console.error('Error loading logs:', err);
            }
        }

        function logPage(delta) {
            logOffset = Math.max(0, logOffset + delta * logLimit);
            loadLogs();
        }

        // Model config editor
        let modelConfigs = [];

        async function loadModelConfigs() {
            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const base = key ? '/api/dashboard/models?key=' + encodeURIComponent(key) : '/api/dashboard/models';
                const opts = key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };
                const response = await fetch(base, opts);
                if (!response.ok) return;
                modelConfigs = await response.json();
                renderModelConfigs();
            } catch (err) {
                console.error('Error loading model configs:', err);
            }
        }

        function renderModelConfigs() {
            const tbody = document.getElementById('config-body');
            tbody.innerHTML = '';

            modelConfigs.forEach((m, idx) => {
                const tr = document.createElement('tr');
                tr.innerHTML = `
                    <td><strong>${m.name}</strong></td>
                    <td>
                        <select id=""cfg-tier-${idx}"">
                            ${['Strong','Medium','Cheap'].map(t => `<option value=""${t}"" ${m.tier===t?'selected':''}>${t}</option>`).join('')}
                        </select>
                    </td>
                    <td><input type=""number"" id=""cfg-ctx-${idx}"" value=""${m.maxContextTokens}"" min=""1""></td>
                    <td><input type=""number"" id=""cfg-timeout-${idx}"" value=""${m.timeoutSeconds}"" min=""1""></td>
                    <td><input type=""number"" id=""cfg-retry-${idx}"" value=""${m.maxRetries}"" min=""0""></td>
                    <td>
                        <select id=""cfg-enabled-${idx}"">
                            <option value=""true"" ${m.enabled?'selected':''}>Yes</option>
                            <option value=""false"" ${!m.enabled?'selected':''}>No</option>
                        </select>
                    </td>
                    <td><input type=""number"" id=""cfg-inp-${idx}"" value=""${m.inputPricePerMillion}"" min=""0"" step=""0.01""></td>
                    <td><input type=""number"" id=""cfg-out-${idx}"" value=""${m.outputPricePerMillion}"" min=""0"" step=""0.01""></td>
                    <td><button class=""save-btn"" onclick=""saveModelConfig(${idx}, '${m.name.replace(/'/g, ""\\'"")}')"">Save</button></td>
                `;
                tbody.appendChild(tr);
            });
        }

        async function saveModelConfig(idx, name) {
            const req = {
                tier: document.getElementById(`cfg-tier-${idx}`).value,
                maxContextTokens: parseInt(document.getElementById(`cfg-ctx-${idx}`).value) || 0,
                timeoutSeconds: parseInt(document.getElementById(`cfg-timeout-${idx}`).value) || 0,
                maxRetries: parseInt(document.getElementById(`cfg-retry-${idx}`).value) || 0,
                enabled: document.getElementById(`cfg-enabled-${idx}`).value === 'true',
                inputPricePerMillion: parseFloat(document.getElementById(`cfg-inp-${idx}`).value) || 0,
                outputPricePerMillion: parseFloat(document.getElementById(`cfg-out-${idx}`).value) || 0
            };

            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const base = key ? '/api/dashboard/models/${encodeURIComponent(name)}?key=' + encodeURIComponent(key) : `/api/dashboard/models/${encodeURIComponent(name)}`;
                const opts = key ? {} : { headers: { 'Authorization': 'Bearer ' + (window.__apiKey || '') } };

                const response = await fetch(base, {
                    method: 'PUT',
                    headers: Object.assign({ 'Content-Type': 'application/json' }, opts.headers || {}),
                    body: JSON.stringify(req)
                });

                if (!response.ok) {
                    const err = await response.json();
                    throw new Error(err.error || 'Update failed');
                }

                showToast('Model updated successfully', 'success');
                loadModelConfigs();
            } catch (err) {
                showToast(err.message, 'error');
            }
        }

        function showToast(message, type) {
            const toast = document.getElementById('config-toast');
            toast.textContent = message;
            toast.className = 'config-toast ' + type;
            setTimeout(() => { toast.className = 'config-toast'; }, 3000);
        }

        // Auto Refresh
        setInterval(fetchMetrics, 2000);
        window.addEventListener('DOMContentLoaded', () => {
            fetchMetrics();
            loadModelConfigs();
        });
    </script>
</body>
</html>";
    }
}
