using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using System.Text.Json;

namespace OptiRouter.Endpoints;

/// <summary>
/// 提供内置的可视化配置和健康状态监控 Dashboard 页面及 API 接口。
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

        // 2. Dashboard Live Metrics API
        endpoints.MapGet("/api/dashboard/metrics", (
            CostLedger ledger,
            ModelHealthTracker tracker,
            IOptions<RouterOptions> options) =>
        {
            var opt = options.Value;
            var circuitSnapshot = tracker.GetCircuitsSnapshot();

            var modelsList = opt.Models.Select(m => new
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

            var spend = ledger.GetSpend();

            return Results.Json(new
            {
                System = new
                {
                    Time = DateTime.UtcNow,
                    RoutingPolicy = opt.Routing,
                    Budget = new
                    {
                        DailyBudgetUsd = opt.Budget.DailyBudgetUsd,
                        opt.Budget.UsePersistentStore,
                        DailySpend = spend.Daily,
                        TotalSpend = spend.Total
                    }
                },
                Models = modelsList
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        });
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

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }

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

        .logo-area {
            display: flex;
            align-items: center;
            gap: 0.75rem;
        }

        .logo-icon {
            width: 2.2rem;
            height: 2.2rem;
            background: linear-gradient(135deg, var(--primary), #8b5cf6);
            border-radius: 0.5rem;
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: 0 0 15px rgba(99, 102, 241, 0.4);
            font-weight: 700;
            font-size: 1.2rem;
        }

        .logo-title {
            font-size: 1.4rem;
            font-weight: 700;
            letter-spacing: -0.025em;
            background: linear-gradient(to right, #ffffff, #c7d2fe);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .system-time {
            font-family: 'JetBrains Mono', monospace;
            font-size: 0.85rem;
            color: var(--text-secondary);
            background: rgba(255, 255, 255, 0.03);
            padding: 0.4rem 0.8rem;
            border-radius: 0.375rem;
            border: 1px solid var(--border);
        }

        main {
            max-width: 1400px;
            margin: 0 auto;
            padding: 2rem;
            display: grid;
            grid-template-columns: 1fr;
            gap: 2rem;
        }

        @media (min-width: 1024px) {
            main {
                grid-template-columns: 280px 1fr;
            }
        }

        .sidebar {
            display: flex;
            flex-direction: column;
            gap: 1.5rem;
        }

        .glass-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 0.75rem;
            padding: 1.5rem;
            backdrop-filter: blur(16px);
            box-shadow: 0 8px 32px 0 rgba(0, 0, 0, 0.3);
            transition: border-color 0.3s, transform 0.3s;
        }

        .glass-card:hover {
            border-color: var(--border-hover);
        }

        .sidebar-title {
            font-size: 0.8rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
            color: var(--text-secondary);
            margin-bottom: 1rem;
            font-weight: 600;
        }

        .stat-value {
            font-size: 2rem;
            font-weight: 700;
            font-family: 'JetBrains Mono', monospace;
            margin: 0.25rem 0;
            color: #fff;
        }

        .budget-bar {
            width: 100%;
            height: 6px;
            background: rgba(255, 255, 255, 0.05);
            border-radius: 3px;
            margin-top: 0.75rem;
            overflow: hidden;
        }

        .budget-progress {
            height: 100%;
            background: linear-gradient(to right, var(--primary), #8b5cf6);
            width: 0%;
            transition: width 0.8s cubic-bezier(0.4, 0, 0.2, 1);
        }

        .budget-text {
            display: flex;
            justify-content: space-between;
            font-size: 0.8rem;
            color: var(--text-secondary);
            margin-top: 0.5rem;
        }

        .models-container {
            display: grid;
            grid-template-columns: 1fr;
            gap: 1.5rem;
        }

        @media (min-width: 768px) {
            .models-container {
                grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
            }
        }

        .model-card {
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            position: relative;
            overflow: hidden;
        }

        .model-card::after {
            content: '';
            position: absolute;
            top: 0;
            left: 0;
            width: 4px;
            height: 100%;
            background: var(--success);
        }

        .model-card.state-closed::after { background: var(--success); }
        .model-card.state-open::after { background: var(--danger); }
        .model-card.state-halfopen::after { background: var(--warning); }

        .card-header-row {
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            margin-bottom: 1rem;
        }

        .model-name {
            font-size: 1.25rem;
            font-weight: 600;
            letter-spacing: -0.01em;
        }

        .status-badge {
            font-size: 0.75rem;
            padding: 0.25rem 0.6rem;
            border-radius: 2rem;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.025em;
        }

        .status-badge.closed {
            background-color: var(--success-glow);
            color: var(--success);
            border: 1px solid rgba(16, 185, 129, 0.2);
        }

        .status-badge.open {
            background-color: var(--danger-glow);
            color: var(--danger);
            border: 1px solid rgba(239, 68, 68, 0.2);
        }

        .status-badge.halfopen {
            background-color: var(--warning-glow);
            color: var(--warning);
            border: 1px solid rgba(245, 158, 11, 0.2);
        }

        .info-grid {
            display: grid;
            grid-template-columns: repeat(2, 1fr);
            gap: 0.75rem;
            font-size: 0.85rem;
            border-bottom: 1px solid var(--border);
            padding-bottom: 1rem;
            margin-bottom: 1rem;
        }

        .info-label {
            color: var(--text-secondary);
        }

        .info-val {
            font-family: 'JetBrains Mono', monospace;
            font-weight: 500;
            color: #fff;
            text-align: right;
        }

        .metrics-row {
            display: flex;
            justify-content: space-between;
            align-items: center;
            font-size: 0.85rem;
        }

        .metric-item {
            display: flex;
            flex-direction: column;
            gap: 0.25rem;
        }

        .metric-lbl {
            font-size: 0.75rem;
            color: var(--text-secondary);
            text-transform: uppercase;
            font-weight: 500;
        }

        .metric-val {
            font-family: 'JetBrains Mono', monospace;
            font-size: 1rem;
            font-weight: 600;
        }

        .indicator {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            display: inline-block;
        }

        .pulse-active {
            animation: pulse 2s infinite;
        }

        @keyframes pulse {
            0% { transform: scale(0.9); opacity: 0.6; }
            50% { transform: scale(1.1); opacity: 1; }
            100% { transform: scale(0.9); opacity: 0.6; }
        }

        .refresh-btn {
            background: var(--primary);
            color: #fff;
            border: none;
            padding: 0.5rem 1rem;
            border-radius: 0.375rem;
            font-weight: 500;
            cursor: pointer;
            font-family: inherit;
            font-size: 0.85rem;
            transition: filter 0.2s;
        }

        .refresh-btn:hover {
            filter: brightness(1.15);
        }

        .banner-alert {
            background: var(--primary-glow);
            border: 1px solid rgba(99, 102, 241, 0.2);
            padding: 1rem;
            border-radius: 0.75rem;
            font-size: 0.9rem;
            margin-bottom: 1.5rem;
            display: flex;
            align-items: center;
            gap: 0.75rem;
        }
    </style>
</head>
<body>
    <header>
        <div class=""logo-area"">
            <div class=""logo-icon"">Ω</div>
            <div class=""logo-title"">OptiRouter</div>
        </div>
        <div style=""display: flex; align-items: center; gap: 1rem;"">
            <button class=""refresh-btn"" onclick=""fetchMetrics()"">Manual Refresh</button>
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

            <div class=""models-container"" id=""models-grid"">
                <!-- Dynamic Model Cards Rendered Here -->
            </div>
        </div>
    </main>

    <script>
        async function fetchMetrics() {
            try {
                const params = new URLSearchParams(window.location.search);
                const key = params.get('key');
                const url = key
                    ? '/api/dashboard/metrics?key=' + encodeURIComponent(key)
                    : '/api/dashboard/metrics';
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
            // Update time
            const sysTime = new Date(data.system.time);
            document.getElementById('utc-time').textContent = 'UTC: ' + sysTime.toISOString().split('T')[1].substring(0, 8);

            // Update Budget
            const budget = data.system.budget;
            const dailySpend = budget.dailySpend || 0;
            const limit = budget.dailyBudgetUsd || 10;
            const percent = limit > 0 ? (dailySpend / limit) * 100 : 0;

            document.getElementById('daily-spend').textContent = '$' + dailySpend.toFixed(6);
            document.getElementById('total-spend').textContent = '$' + (budget.totalSpend || 0).toFixed(6);
            document.getElementById('budget-bar-fill').style.width = Math.min(percent, 100) + '%';
            document.getElementById('budget-percent').textContent = percent.toFixed(2) + '% Used';
            document.getElementById('budget-limit').textContent = 'Max $' + limit.toFixed(2);

            // Update Routing Engine Info
            const policy = data.system.routingPolicy;
            document.getElementById('engine-failover').textContent = policy.enableFailover ? 'Active' : 'Disabled';
            document.getElementById('engine-failover').style.color = policy.enableFailover ? 'var(--success)' : 'var(--text-secondary)';
            document.getElementById('engine-budget').textContent = policy.enableBudgetGuard ? 'Active' : 'Disabled';
            document.getElementById('engine-budget').style.color = policy.enableBudgetGuard ? 'var(--success)' : 'var(--text-secondary)';
            document.getElementById('engine-classifier').textContent = policy.enableRuleClassifier ? 'Active' : 'Disabled';
            document.getElementById('engine-classifier').style.color = policy.enableRuleClassifier ? 'var(--success)' : 'var(--text-secondary)';

            // Render Models Grid
            const grid = document.getElementById('models-grid');
            grid.innerHTML = '';

            data.models.forEach(model => {
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

        // Auto Refresh
        setInterval(fetchMetrics, 2000);
        window.addEventListener('DOMContentLoaded', fetchMetrics);
    </script>
</body>
</html>";
    }
}
