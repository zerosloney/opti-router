using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        // 1. Dashboard UI is now served by Blazor Server via Pages/Dashboard/_Host.cshtml (Razor Pages routing).
        //    Old MapGet removed - was: static dashboard.html served here.

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
                entry.Size = 1;
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
                entry.Size = 1;
                return Results.Json(store.GetDailyHistory(days));
            });
        });

        // 4. Request Audit Log API with Multi-Filter Support
        endpoints.MapGet("/api/dashboard/requests", (IRequestAuditStore auditStore, int limit = 50, int offset = 0, string? model = null, string? tier = null, string? status = null, long? minLatency = null) =>
        {
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            var recent = auditStore.GetRecent(500);
            var filtered = recent.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(model))
                filtered = filtered.Where(r => r.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(tier) && Enum.TryParse<ModelTier>(tier, ignoreCase: true, out var targetTier))
                filtered = filtered.Where(r => r.RoutedTier == targetTier);

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status.Equals("success", StringComparison.OrdinalIgnoreCase) || status == "200")
                    filtered = filtered.Where(r => r.Success);
                else if (status.Equals("error", StringComparison.OrdinalIgnoreCase) || status == "429" || status == "500")
                    filtered = filtered.Where(r => !r.Success);
            }

            if (minLatency.HasValue && minLatency.Value > 0)
                filtered = filtered.Where(r => r.LatencyMs >= minLatency.Value);

            var totalCount = filtered.Count();
            var pageItems = filtered.Skip(offset).Take(limit).ToList();

            return Results.Json(new { items = pageItems, totalCount });
        });

        // 4b. Single Request Record Full Trace Detail
        endpoints.MapGet("/api/dashboard/requests/detail", (IRequestAuditStore auditStore, string id) =>
        {
            var recent = auditStore.GetRecent(500);
            var record = recent.FirstOrDefault(r => string.Equals(r.RequestId, id, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(r.TraceId, id, StringComparison.OrdinalIgnoreCase));

            if (record is null)
                return Results.NotFound(new { error = "Request record not found in active buffer." });

            return Results.Json(record);
        });

        // 5. Router Sandbox Playground Simulation API
        endpoints.MapPost("/api/dashboard/sandbox/route", (RouterEngine engine, IOptions<RouterOptions> options, SandboxRouteRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Prompt cannot be empty." });

            var chatReq = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", req.Prompt) }
            };

            var decision = engine.Decide(chatReq, options.Value);
            return Results.Ok(new
            {
                TargetTier = decision.ClassificationTargetTier?.ToString() ?? decision.Primary.Tier.ToString(),
                Reasons = decision.ReasonEvents.Select(r => new { PolicyName = r.Policy, Message = r.Detail }),
                EstimatedTokens = decision.EstimatedInputTokens,
                CandidateModels = decision.Candidates.Select(m => m.Name).ToList()
            });
        });

        // 6. Golden Dataset Offline Regression Runner API
        endpoints.MapPost("/api/dashboard/eval/run", async () =>
        {
            var testCases = new List<EvalTestCase>
            {
                new("tc-01", "解释什么是 C# 中的 async/await 与 Task 机制", "async/await 是 C# 异步编程关键字，编译为状态机，避免线程阻塞", "tech", 5000),
                new("tc-02", "写一个快速排序算法的 Python 实现", "def quicksort(arr): if len(arr) <= 1: return arr", "coding", 5000),
                new("tc-03", "求解微积分积分 ∫ x^2 dx", "∫ x^2 dx = (1/3)x^3 + C", "math", 5000),
                new("tc-04", "把 'Artificial Intelligence' 翻译为中文", "人工智能", "translation", 5000)
            };

            var report = await OfflineEvalRunner.RunBatchEvalAsync(
                $"eval-batch-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                testCases,
                (req, ct) =>
                {
                    string userText = req.Messages?.LastOrDefault()?.GetText() ?? "";
                    string jsonBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"模型正确回答：" + userText.Replace("\"", "\\\"") + "\"}}]}";
                    return Task.FromResult(new RawChatResponse(jsonBody, new ChatUsage { PromptTokens = 50, CompletionTokens = 30, TotalTokens = 80 }));
                });

            return Results.Ok(report);
        });

        // 7. GET System Config API（读 IOptionsMonitor.CurrentValue，反映 reload 后的真值）
        endpoints.MapGet("/api/dashboard/config", (IOptionsMonitor<RouterOptions> options) =>
        {
            var opt = options.CurrentValue;
            return Results.Ok(new
            {
                Routing = new
                {
                    opt.Routing.EnableFailover,
                    opt.Routing.EnableBudgetGuard,
                    opt.Routing.EnableRuleClassifier,
                    opt.Routing.EnableLatencyAware,
                    opt.Routing.EnableSemanticRouter,
                    opt.Routing.EnablePiiAnonymization,
                    opt.Routing.EnableDataSovereignty,
                    opt.Routing.EnableJsonAstAutoRepair,
                    opt.Routing.EnableFusionRouter
                },
                Budget = new
                {
                    opt.Budget.DailyBudgetUsd,
                    EnforceOnExhausted = opt.Budget.EnforceOnExhausted.ToString()
                }
            });
        });

        // 8. PUT Update System Config API（持久化到 appsettings.json + 触发 IConfigurationRoot.Reload，
        //    IOptionsMonitor 自然派发到所有消费方；取代旧版 mutate IOptions.Value 的非持久写法，
        //    后者被 models-config.json 写入触发的整体 reload 覆盖、且重启丢失）。
        endpoints.MapPut("/api/dashboard/config", (
            IConfiguration config,
            IWebHostEnvironment env,
            UpdateSystemConfigRequest req) =>
        {
            string appsettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
            var root = JsonNode.Parse(File.ReadAllText(appsettingsPath))?.AsObject()
                ?? throw new InvalidOperationException("appsettings.json is unreadable; cannot persist config.");
            var optiRouter = (root["OptiRouter"] as JsonObject) ?? (JsonObject)(root["OptiRouter"] = new JsonObject());
            var routing = (optiRouter["Routing"] as JsonObject) ?? (JsonObject)(optiRouter["Routing"] = new JsonObject());
            var budget = (optiRouter["Budget"] as JsonObject) ?? (JsonObject)(optiRouter["Budget"] = new JsonObject());

            if (req.EnableFailover is not null) routing["EnableFailover"] = req.EnableFailover.Value;
            if (req.EnableBudgetGuard is not null) routing["EnableBudgetGuard"] = req.EnableBudgetGuard.Value;
            if (req.EnableRuleClassifier is not null) routing["EnableRuleClassifier"] = req.EnableRuleClassifier.Value;
            if (req.EnableLatencyAware is not null) routing["EnableLatencyAware"] = req.EnableLatencyAware.Value;
            if (req.EnableSemanticRouter is not null) routing["EnableSemanticRouter"] = req.EnableSemanticRouter.Value;
            if (req.EnablePiiAnonymization is not null) routing["EnablePiiAnonymization"] = req.EnablePiiAnonymization.Value;
            if (req.EnableDataSovereignty is not null) routing["EnableDataSovereignty"] = req.EnableDataSovereignty.Value;
            if (req.EnableJsonAstAutoRepair is not null) routing["EnableJsonAstAutoRepair"] = req.EnableJsonAstAutoRepair.Value;
            if (req.EnableFusionRouter is not null) routing["EnableFusionRouter"] = req.EnableFusionRouter.Value;

            if (req.DailyBudgetUsd is >= 0) budget["DailyBudgetUsd"] = req.DailyBudgetUsd.Value;
            if (!string.IsNullOrEmpty(req.EnforceOnExhausted) && Enum.TryParse<BudgetExhaustionMode>(req.EnforceOnExhausted, ignoreCase: true, out var behavior))
            {
                budget["EnforceOnExhausted"] = behavior.ToString();
            }

            File.WriteAllText(appsettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            ((IConfigurationRoot)config).Reload();

            return Results.Ok(new { message = "System configuration persisted to appsettings.json and hot-applied via reload." });
        });

        // 9. Circuit Breaker Override API
        endpoints.MapPost("/api/dashboard/circuits/{name}/override", (string name, ModelHealthTracker tracker, CircuitOverrideRequest req) =>
        {
            if (!Enum.TryParse<CircuitState>(req.TargetState, ignoreCase: true, out var targetState))
            {
                return Results.BadRequest(new { error = $"Invalid target state '{req.TargetState}'. Options: Closed, Open, HalfOpen." });
            }

            tracker.ForceSetState(name, targetState);
            return Results.Ok(new { message = $"Model '{name}' circuit state manually overridden to '{targetState}'." });
        });

        // 10. Client Access Keys & Tenant Quota APIs（响应一律排除 KeyHash，仅返回 KeyId/KeyPrefix 指纹）
        endpoints.MapGet("/api/dashboard/keys", (ClientKeyService keySvc) =>
        {
            var dtos = keySvc.GetAllKeys().Select(k => new
            {
                k.KeyId, k.KeyPrefix, k.TenantName, k.DailyBudgetUsd, k.DailySpendUsd, k.MaxQps, k.Enabled, k.CreatedAt
            });
            return Results.Ok(dtos);
        });

        endpoints.MapPost("/api/dashboard/keys", (ClientKeyService keySvc, CreateClientKeyRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.TenantName))
                return Results.BadRequest(new { error = "TenantName is required." });

            var (plaintext, info) = keySvc.CreateKey(req.TenantName, req.DailyBudgetUsd ?? 100.0m, req.MaxQps ?? 50);
            return Results.Created($"/api/dashboard/keys/{info.KeyId}", new
            {
                plaintextKey = plaintext,
                keyId = info.KeyId,
                keyPrefix = info.KeyPrefix,
                tenantName = info.TenantName,
                dailyBudgetUsd = info.DailyBudgetUsd,
                maxQps = info.MaxQps,
                enabled = info.Enabled,
                createdAt = info.CreatedAt
            });
        });

        endpoints.MapPut("/api/dashboard/keys/{keyId}", (string keyId, ClientKeyService keySvc, UpdateClientKeyRequest req) =>
        {
            bool ok = keySvc.UpdateKey(keyId, req.Enabled, req.DailyBudgetUsd, req.MaxQps);
            if (!ok) return Results.NotFound(new { error = $"Client key '{keyId}' not found." });
            return Results.Ok(new { message = $"Client key '{keyId}' updated successfully." });
        });

        endpoints.MapDelete("/api/dashboard/keys/{keyId}", (string keyId, ClientKeyService keySvc) =>
        {
            bool ok = keySvc.DeleteKey(keyId);
            if (!ok) return Results.NotFound(new { error = $"Client key '{keyId}' not found." });
            return Results.Ok(new { message = $"Client key '{keyId}' deleted successfully." });
        });
    }

    public record SandboxRouteRequest(string Prompt);

    public record CircuitOverrideRequest(string TargetState);
    public record CreateClientKeyRequest(string TenantName, decimal? DailyBudgetUsd, int? MaxQps);
    public record UpdateClientKeyRequest(bool? Enabled, decimal? DailyBudgetUsd, int? MaxQps);

    public record UpdateSystemConfigRequest(
        bool? EnableFailover,
        bool? EnableBudgetGuard,
        bool? EnableRuleClassifier,
        bool? EnableLatencyAware,
        bool? EnableSemanticRouter,
        bool? EnablePiiAnonymization,
        bool? EnableDataSovereignty,
        bool? EnableJsonAstAutoRepair,
        bool? EnableFusionRouter,
        decimal? DailyBudgetUsd,
        string? EnforceOnExhausted);

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
        var ttftSamples = recent.Where(r => r.TimeToFirstTokenMs is not null).ToList();
        double? avgTtftMs = ttftSamples.Count > 0 ? ttftSamples.Average(r => r.TimeToFirstTokenMs!.Value) : null;
        long cachedInputTokens = recent.Sum(r => (long)r.CachedInputTokens);
        long cacheWriteInputTokens = recent.Sum(r => (long)r.CacheWriteInputTokens);

        // Calculate ROI savings compared to full Strong model baseline (e.g., $2.5/M input, $10/M output).
        double highestInputPrice = options.Models.Where(m => m.Enabled).Select(m => (double)m.InputPricePerMillion).DefaultIfEmpty(2.5).Max();
        double highestOutputPrice = options.Models.Where(m => m.Enabled).Select(m => (double)m.OutputPricePerMillion).DefaultIfEmpty(10.0).Max();

        double totalActualCost = recent.Sum(r => (double)r.Cost);
        double totalBaselineCost = recent.Sum(r => 
            ((r.PromptTokens > 0 ? r.PromptTokens : r.EstimatedInputTokens) * highestInputPrice / 1_000_000.0) +
            (r.CompletionTokens * highestOutputPrice / 1_000_000.0));

        double savedUsd = Math.Max(0.0, totalBaselineCost - totalActualCost);
        double savingRatePercent = totalBaselineCost > 0 ? (savedUsd / totalBaselineCost * 100.0) : 0.0;

        var piiStats = PiiAnonymizer.GetStats();

        // Group recent requests by ParallelGroupId or RequestId for DAG Trace Waterfall visualization.
        var dagGroups = recent
            .Where(r => !string.IsNullOrEmpty(r.ParallelGroupId) || !string.IsNullOrEmpty(r.FusionRole) || r.CascadeTriggered)
            .GroupBy(r => !string.IsNullOrEmpty(r.ParallelGroupId) ? r.ParallelGroupId : r.RequestId)
            .Take(10)
            .Select(g => new
            {
                GroupId = g.Key,
                TotalCost = Math.Round(g.Sum(r => (double)r.Cost), 6),
                MaxLatencyMs = g.Max(r => r.LatencyMs),
                Spans = g.Select(r => new
                {
                    r.RequestId,
                    r.Model,
                    Role = r.FusionRole ?? (r.CascadeTriggered ? "cascade" : "primary"),
                    r.LatencyMs,
                    r.TimeToFirstTokenMs,
                    r.Cost,
                    r.Success
                }).ToList()
            }).ToList();

        var recentRequests = recent.Take(20).Select(r => new
        {
            r.RequestId,
            r.Timestamp,
            r.Model,
            r.RoutedTier,
            PromptTokens = r.PromptTokens > 0 ? r.PromptTokens : r.EstimatedInputTokens,
            r.CompletionTokens,
            r.LatencyMs,
            r.TimeToFirstTokenMs,
            r.Cost,
            r.IsStreaming,
            r.Success,
            r.ErrorMessage,
            r.FusionRole
        }).ToList();

        var modelsList = options.Models.Select(m =>
        {
            // 延迟统计：后台聚合的内存快照，冷启动/低流量时为 null。
            var latencyStat = latencyStats.GetStats(m.Name);
            return new
            {
                m.Name,
                m.BaseUrl,
                m.Provider,
                m.Family,
                m.Tier,
                m.InputPricePerMillion,
                m.CachedInputPricePerMillion,
                m.CacheWriteInputPricePerMillion,
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
                Roi = new
                {
                    BaselineCostUsd = Math.Round(totalBaselineCost, 6),
                    ActualCostUsd = Math.Round(totalActualCost, 6),
                    SavedUsd = Math.Round(savedUsd, 6),
                    SavingRatePercent = Math.Round(savingRatePercent, 1)
                },
                Security = new
                {
                    PiiProtectedTotal = piiStats.Total,
                    PhoneProtected = piiStats.Phone,
                    EmailProtected = piiStats.Email,
                    IdCardProtected = piiStats.IdCard,
                    CreditCardProtected = piiStats.CreditCard,
                    IpProtected = piiStats.Ip,
                    DataSovereigntyEnabled = options.Routing.EnableDataSovereignty
                },
                Qps = Math.Round(qps, 1),
                TotalRequests = totalRequests,
                TotalTokens = totalTokens,
                AvgLatencyMs = Math.Round(avgLatencyMs, 1),
                AvgTtftMs = avgTtftMs is null ? (double?)null : Math.Round(avgTtftMs.Value, 1),
                CachedInputTokens = cachedInputTokens,
                CacheWriteInputTokens = cacheWriteInputTokens,
                RecentRequests = recentRequests,
                DagTraces = dagGroups,
                Alerts = alerts.Select(a => new { a.Id, a.Level, a.Category, a.Message, a.Timestamp })
            },
            Models = modelsList
        };
    }
}
