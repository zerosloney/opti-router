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
        // 1. Dashboard UI Main View (served from static file)
        endpoints.MapGet("/dashboard", (IWebHostEnvironment env) =>
        {
            string path = Path.Combine(env.ContentRootPath, "dashboard.html");
            if (!File.Exists(path))
                return Results.NotFound();
            return Results.File(path, "text/html; charset=utf-8");
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
        long totalTokens = recent.Sum(r => (long)r.PromptTokens + r.CompletionTokens);
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
}