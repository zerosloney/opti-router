using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
            IOptionsMonitor<RouterOptions> options,
            IMemoryCache cache) =>
        {
            return cache.GetOrCreate("dashboard:metrics", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                entry.Size = 1;
                return ComputeMetrics(ledger, tracker, auditStore, alertEngine, latencyStats, options.CurrentValue);
            });
        });

        // 2b. Window Summary API (cached 1s) — 多窗口统计（输入/输出 token、缓存命中率、错误率等）
        endpoints.MapGet("/api/dashboard/metrics/summary", (
            IRequestAuditStore auditStore,
            IOptionsMonitor<RouterOptions> options,
            IMemoryCache cache,
            string? window) =>
        {
            string key = NormalizeWindow(window);
            return cache.GetOrCreate($"dashboard:summary:{key}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                entry.Size = 1;
                return ComputeWindowSummary(auditStore, options.CurrentValue, key);
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

        // 3b. Learning State API (cached 5s) — Thompson 学习状态快照（α/β/样本数/最后更新）。
        // 暴露低流量下的"尾部锁死"：samples 长期为 0 的模型拿不到流量，学习重排对它无据可依，
        // 需配合 ExplorationEpsilon 或流量再分配。样本数为进程内计数（重启归零）。
        endpoints.MapGet("/api/dashboard/learning", (ThompsonStateStore tsStore, IMemoryCache cache) =>
        {
            return cache.GetOrCreate("dashboard:learning", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
                entry.Size = 1;
                return Results.Json(tsStore.GetSnapshot().Select(s => new
                {
                    model = s.Model,
                    alpha = Math.Round(s.Alpha, 4),
                    beta = Math.Round(s.Beta, 4),
                    mean = Math.Round(s.Mean, 4),
                    samples = s.N,
                    lastUpdateUtc = s.LastUpdateUtc
                }));
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
        endpoints.MapPost("/api/dashboard/sandbox/route", (RouterEngine engine, IOptionsMonitor<RouterOptions> options, SandboxRouteRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Prompt cannot be empty." });

            var chatReq = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", req.Prompt) }
            };

            var decision = engine.Decide(chatReq, options.CurrentValue);
            return Results.Ok(new
            {
                TargetTier = decision.ClassificationTargetTier?.ToString() ?? decision.Primary.Tier.ToString(),
                Reasons = decision.ReasonEvents.Select(r => new { PolicyName = r.Policy, Message = r.Detail }),
                EstimatedTokens = decision.EstimatedInputTokens,
                CandidateModels = decision.Candidates.Select(m => m.Name).ToList()
            });
        });

        // 6. Golden Dataset Offline Regression Runner API
        //    经 ProxyOrchestrator.SendAsync 走完整真实管线（路由→上游→计费→审计），
        //    取代旧桩实现（伪造固定回复，评测结果无意义）。Cases 为空时回落内置题库。
        endpoints.MapPost("/api/dashboard/eval/run", async (
            ProxyOrchestrator orchestrator,
            IMemoryCache cache,
            EvalRunRequest? req) =>
        {
            var dataset = BuildEvalDataset(req?.Cases);
            if (dataset.Count == 0)
            {
                return Results.BadRequest(new { error = "Cases 为空或全部非法：每条需含非空 question 与 expectedAnswer，上限 50 条。" });
            }

            var report = await OfflineEvalRunner.RunBatchEvalAsync(
                $"eval-batch-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                dataset,
                // SendAsync 带可选 sessionId 参数，方法组无法直接转换为二元委托，显式适配。
                (request, token) => orchestrator.SendAsync(request, token));

            RecordEvalBatch(cache, report);
            return Results.Ok(report);
        });

        // 6b. Eval Batch History API（进程内保留最近 10 批，重启清空）
        endpoints.MapGet("/api/dashboard/eval/batches", (IMemoryCache cache) =>
        {
            var batches = GetEvalHistory(cache)
                .OrderByDescending(r => r.Timestamp)
                .ToList();
            return Results.Ok(batches);
        });

        // 6c. Paired A/B Compare API——复用 OfflineEvalRunner.Compare 按用例 ID 成对比较两个批次
        endpoints.MapPost("/api/dashboard/eval/compare", (IMemoryCache cache, EvalCompareRequest req) =>
        {
            var history = GetEvalHistory(cache);
            var baseline = history.FirstOrDefault(r => string.Equals(r.BatchId, req.BaselineBatchId, StringComparison.Ordinal));
            var candidate = history.FirstOrDefault(r => string.Equals(r.BatchId, req.CandidateBatchId, StringComparison.Ordinal));
            if (baseline is null || candidate is null)
            {
                return Results.NotFound(new { error = "批次不存在。评测历史为进程内状态（重启清空），请先运行评测。" });
            }

            return Results.Ok(OfflineEvalRunner.Compare(baseline, candidate));
        });

        // 10b. Internal Routing State APIs（进程内状态可见性：上游配额 / 缓存亲和 / 响应缓存）
        endpoints.MapGet("/api/dashboard/state/quota", (UpstreamQuotaStateStore quotaStore) =>
        {
            var now = DateTimeOffset.UtcNow;
            var items = quotaStore.GetAllSnapshots()
                .OrderByDescending(s => s.IsExhausted(now))
                .ThenBy(s => s.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(s => new
                {
                    s.ModelName,
                    s.RequestsRemaining,
                    s.TokensRemaining,
                    s.RequestsResetAt,
                    s.TokensResetAt,
                    s.ExhaustedUntil,
                    s.LastStatusCode,
                    s.ObservedAt,
                    IsExhausted = s.IsExhausted(now)
                });
            return Results.Ok(new { items });
        });

        endpoints.MapGet("/api/dashboard/state/cache-affinity", (PromptCacheAffinityStore store) =>
        {
            var entries = store.GetEntries();
            return Results.Ok(new
            {
                totalCount = entries.Count,
                // 条目可达上万，只下发最近 50 条；指纹已是最短可辨前缀展示由前端截断
                items = entries.Take(50)
            });
        });

        endpoints.MapGet("/api/dashboard/state/response-cache", (MemoryResponseCache responseCache) =>
        {
            var (hits, misses, sets, current, max) = responseCache.GetStats();
            long total = hits + misses;
            return Results.Ok(new
            {
                hits,
                misses,
                sets,
                currentEntries = current,
                maxEntries = max,
                hitRatePercent = total > 0 ? (double)hits / total * 100.0 : 0.0
            });
        });

        // 10c. Semantic Routes Management APIs——语义路由此前是唯一只能手改配置文件的路由策略。
        //     SemanticRouterPolicy 每请求从 context.Options 读取路由表，reload 后立即热生效。
        endpoints.MapGet("/api/dashboard/semantic-routes", (IOptionsMonitor<RouterOptions> options) =>
        {
            var opt = options.CurrentValue.Routing;
            var routes = (opt.SemanticRoutes ?? new List<SemanticRouteOptions>())
                .Select(r => new { r.Name, r.Phrases, TargetTier = r.TargetTier.ToString() })
                .ToList();
            return Results.Ok(new
            {
                enabled = opt.EnableSemanticRouter,
                similarityThreshold = opt.SemanticSimilarityThreshold,
                routes
            });
        });

        endpoints.MapPut("/api/dashboard/semantic-routes", (
            IConfiguration config,
            IWebHostEnvironment env,
            UpdateSemanticRoutesRequest req) =>
        {
            var (routes, error) = BuildSemanticRoutes(req.Routes);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            PersistAppsettings(config, env, root =>
            {
                var optiRouter = (root["OptiRouter"] as JsonObject) ?? (JsonObject)(root["OptiRouter"] = new JsonObject());
                var routing = (optiRouter["Routing"] as JsonObject) ?? (JsonObject)(optiRouter["Routing"] = new JsonObject());
                var array = new JsonArray();
                foreach (var route in routes)
                {
                    var phrases = new JsonArray();
                    foreach (var phrase in route.Phrases)
                    {
                        phrases.Add(phrase);
                    }
                    array.Add(new JsonObject
                    {
                        ["name"] = route.Name,
                        ["phrases"] = phrases,
                        ["targetTier"] = route.TargetTier.ToString()
                    });
                }
                routing["SemanticRoutes"] = array;
            });

            return Results.Ok(new { message = $"Semantic routes persisted ({routes.Count} rules) and hot-applied via reload." });
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
                    opt.Routing.EnableFusionRouter,
                    opt.Routing.EnableThompsonSampling,
                    opt.Routing.EnableContextualBandit,
                    opt.Routing.ExplorationEpsilon,
                    opt.Routing.ExplorationStarvedN,
                    opt.Routing.EnableResponseCache,
                    opt.Routing.ResponseCacheTtlSeconds,
                    opt.Routing.ResponseCacheMaxEntries,
                    opt.Routing.FailoverFailureThreshold,
                    opt.Routing.FailoverCooldownSeconds
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
            IOptionsMonitor<RouterOptions> optionsMonitor,
            UpdateSystemConfigRequest req) =>
        {
            // 落盘前把变更应用到"当前配置的克隆"并复用启动校验器（RouterOptionsValidator）：
            // 坏配置一旦落盘，reload 会被 IOptionsMonitor 静默拒绝（表面保存成功、实际未生效），
            // 更糟的是重启时 ValidateOnStart 直接失败导致进程起不来。
            // 克隆 = 从组合配置重新绑定（与启动同源）+ 用权威 models-config.json 的模型列表校正
            // （启动绑定时 Models 由 Configure<ModelsConfigService> 覆盖，纯 config 绑定拿不到）。
            var candidate = new RouterOptions();
            config.GetSection("OptiRouter").Bind(candidate);
            candidate.Models.Clear();
            foreach (var model in optionsMonitor.CurrentValue.Models)
            {
                candidate.Models.Add(model);
            }
            ApplyRequestToOptions(candidate, req);
            var validation = new RouterOptionsValidator().Validate(name: null, options: candidate);
            if (validation.Failed)
            {
                return Results.BadRequest(new { error = string.Join("; ", validation.Failures) });
            }

            PersistAppsettings(config, env, root =>
            {
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
                if (req.EnableThompsonSampling is not null) routing["EnableThompsonSampling"] = req.EnableThompsonSampling.Value;
                if (req.EnableContextualBandit is not null) routing["EnableContextualBandit"] = req.EnableContextualBandit.Value;
                if (req.ExplorationEpsilon is not null) routing["ExplorationEpsilon"] = req.ExplorationEpsilon.Value;
                if (req.ExplorationStarvedN is not null) routing["ExplorationStarvedN"] = req.ExplorationStarvedN.Value;
                if (req.EnableResponseCache is not null) routing["EnableResponseCache"] = req.EnableResponseCache.Value;
                if (req.ResponseCacheTtlSeconds is > 0) routing["ResponseCacheTtlSeconds"] = req.ResponseCacheTtlSeconds.Value;
                if (req.ResponseCacheMaxEntries is > 0) routing["ResponseCacheMaxEntries"] = req.ResponseCacheMaxEntries.Value;
                if (req.FailoverFailureThreshold is > 0) routing["FailoverFailureThreshold"] = req.FailoverFailureThreshold.Value;
                if (req.FailoverCooldownSeconds is > 0) routing["FailoverCooldownSeconds"] = req.FailoverCooldownSeconds.Value;

                if (req.DailyBudgetUsd is >= 0) budget["DailyBudgetUsd"] = req.DailyBudgetUsd.Value;
                if (!string.IsNullOrEmpty(req.EnforceOnExhausted) && Enum.TryParse<BudgetExhaustionMode>(req.EnforceOnExhausted, ignoreCase: true, out var behavior))
                {
                    budget["EnforceOnExhausted"] = behavior.ToString();
                }
            });

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
        bool? EnableThompsonSampling,
        bool? EnableContextualBandit,
        double? ExplorationEpsilon,
        long? ExplorationStarvedN,
        bool? EnableResponseCache,
        int? ResponseCacheTtlSeconds,
        int? ResponseCacheMaxEntries,
        int? FailoverFailureThreshold,
        int? FailoverCooldownSeconds,
        decimal? DailyBudgetUsd,
        string? EnforceOnExhausted);

    /// <summary>
    /// 把 PUT 请求的字段应用到配置克隆上，写入条件与落盘 JsonObject 的分支一一对应，
    /// 确保校验器看到的"候选配置"与实际持久化的内容一致。
    /// </summary>
    private static void ApplyRequestToOptions(RouterOptions candidate, UpdateSystemConfigRequest req)
    {
        var routing = candidate.Routing;
        if (req.EnableFailover is not null) routing.EnableFailover = req.EnableFailover.Value;
        if (req.EnableBudgetGuard is not null) routing.EnableBudgetGuard = req.EnableBudgetGuard.Value;
        if (req.EnableRuleClassifier is not null) routing.EnableRuleClassifier = req.EnableRuleClassifier.Value;
        if (req.EnableLatencyAware is not null) routing.EnableLatencyAware = req.EnableLatencyAware.Value;
        if (req.EnableSemanticRouter is not null) routing.EnableSemanticRouter = req.EnableSemanticRouter.Value;
        if (req.EnablePiiAnonymization is not null) routing.EnablePiiAnonymization = req.EnablePiiAnonymization.Value;
        if (req.EnableDataSovereignty is not null) routing.EnableDataSovereignty = req.EnableDataSovereignty.Value;
        if (req.EnableJsonAstAutoRepair is not null) routing.EnableJsonAstAutoRepair = req.EnableJsonAstAutoRepair.Value;
        if (req.EnableFusionRouter is not null) routing.EnableFusionRouter = req.EnableFusionRouter.Value;
        if (req.EnableThompsonSampling is not null) routing.EnableThompsonSampling = req.EnableThompsonSampling.Value;
        if (req.EnableContextualBandit is not null) routing.EnableContextualBandit = req.EnableContextualBandit.Value;
        if (req.ExplorationEpsilon is not null) routing.ExplorationEpsilon = req.ExplorationEpsilon.Value;
        if (req.ExplorationStarvedN is not null) routing.ExplorationStarvedN = req.ExplorationStarvedN.Value;
        if (req.EnableResponseCache is not null) routing.EnableResponseCache = req.EnableResponseCache.Value;
        if (req.ResponseCacheTtlSeconds is > 0) routing.ResponseCacheTtlSeconds = req.ResponseCacheTtlSeconds.Value;
        if (req.ResponseCacheMaxEntries is > 0) routing.ResponseCacheMaxEntries = req.ResponseCacheMaxEntries.Value;
        if (req.FailoverFailureThreshold is > 0) routing.FailoverFailureThreshold = req.FailoverFailureThreshold.Value;
        if (req.FailoverCooldownSeconds is > 0) routing.FailoverCooldownSeconds = req.FailoverCooldownSeconds.Value;
        if (req.DailyBudgetUsd is >= 0) candidate.Budget.DailyBudgetUsd = req.DailyBudgetUsd.Value;
        if (!string.IsNullOrEmpty(req.EnforceOnExhausted) && Enum.TryParse<BudgetExhaustionMode>(req.EnforceOnExhausted, ignoreCase: true, out var behavior))
        {
            candidate.Budget.EnforceOnExhausted = behavior;
        }
    }

    public record EvalRunRequest(List<EvalCaseRequest>? Cases);

    public record EvalCaseRequest(
        string? Id,
        string? Question,
        string? ExpectedAnswer,
        string? Category = null,
        long? MaxLatencyThresholdMs = null);

    public record EvalCompareRequest(string BaselineBatchId, string CandidateBatchId);

    public record UpdateSemanticRoutesRequest(List<SemanticRouteUpsertRequest>? Routes);

    public record SemanticRouteUpsertRequest(string? Name, List<string>? Phrases, string? TargetTier);

    /// <summary>
    /// 校验并归一化语义路由规则（整表替换语义）：名称唯一必填、每条至少一句 phrase、tier 合法。
    /// 允许空列表 = 清空全部规则（policy 对空表按 disabled 处理）。
    /// </summary>
    private static (List<SemanticRouteOptions> Routes, string? Error) BuildSemanticRoutes(List<SemanticRouteUpsertRequest>? request)
    {
        const int maxRoutes = 100;
        const int maxPhrasesPerRoute = 50;
        if (request is null || request.Count == 0)
        {
            return (new List<SemanticRouteOptions>(), null);
        }
        if (request.Count > maxRoutes)
        {
            return (new List<SemanticRouteOptions>(), $"语义路由规则上限 {maxRoutes} 条。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<SemanticRouteOptions>(request.Count);
        foreach (var r in request)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                return (new List<SemanticRouteOptions>(), "每条规则必须有非空 name。");
            }
            string name = r.Name.Trim();
            if (!seen.Add(name))
            {
                return (new List<SemanticRouteOptions>(), $"规则 name 重复: {name}。");
            }

            var phrases = (r.Phrases ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Take(maxPhrasesPerRoute)
                .ToList();
            if (phrases.Count == 0)
            {
                return (new List<SemanticRouteOptions>(), $"规则 '{name}' 至少需要一条非空 phrase。");
            }

            if (!Enum.TryParse<ModelTier>(r.TargetTier, ignoreCase: true, out var tier))
            {
                return (new List<SemanticRouteOptions>(), $"规则 '{name}' 的 targetTier 非法（Strong/Medium/Cheap）: {r.TargetTier}。");
            }

            routes.Add(new SemanticRouteOptions { Name = name, Phrases = phrases, TargetTier = tier });
        }
        return (routes, null);
    }

    /// <summary>校验并归一化自定义题库；null/空回落内置 4 题黄金集。非法条目直接丢弃，全部非法返回空列表。</summary>
    private static List<EvalTestCase> BuildEvalDataset(List<EvalCaseRequest>? cases)
    {
        const int maxCases = 50;
        if (cases is null || cases.Count == 0)
        {
            return new List<EvalTestCase>
            {
                new("tc-01", "解释什么是 C# 中的 async/await 与 Task 机制", "async/await 是 C# 异步编程关键字，编译为状态机，避免线程阻塞", "tech", 5000),
                new("tc-02", "写一个快速排序算法的 Python 实现", "def quicksort(arr): if len(arr) <= 1: return arr", "coding", 5000),
                new("tc-03", "求解微积分积分 ∫ x^2 dx", "∫ x^2 dx = (1/3)x^3 + C", "math", 5000),
                new("tc-04", "把 'Artificial Intelligence' 翻译为中文", "人工智能", "translation", 5000)
            };
        }

        var dataset = new List<EvalTestCase>(Math.Min(cases.Count, maxCases));
        for (int i = 0; i < cases.Count && dataset.Count < maxCases; i++)
        {
            var c = cases[i];
            if (string.IsNullOrWhiteSpace(c.Question) || string.IsNullOrWhiteSpace(c.ExpectedAnswer)) continue;
            dataset.Add(new EvalTestCase(
                string.IsNullOrWhiteSpace(c.Id) ? $"custom-{i + 1:D2}" : c.Id.Trim(),
                c.Question.Trim(),
                c.ExpectedAnswer.Trim(),
                string.IsNullOrWhiteSpace(c.Category) ? "custom" : c.Category.Trim(),
                c.MaxLatencyThresholdMs is > 0 ? c.MaxLatencyThresholdMs.Value : 5000));
        }
        return dataset;
    }

    private const string EvalHistoryCacheKey = "dashboard:eval-history";
    private const int EvalHistoryMaxBatches = 10;
    private static readonly object EvalHistoryLock = new();

    private static List<BatchEvalReport> GetEvalHistory(IMemoryCache cache)
        => cache.Get<List<BatchEvalReport>>(EvalHistoryCacheKey) ?? new List<BatchEvalReport>();

    private static void RecordEvalBatch(IMemoryCache cache, BatchEvalReport report)
    {
        lock (EvalHistoryLock)
        {
            var history = cache.GetOrCreate(EvalHistoryCacheKey, entry =>
            {
                entry.Size = 1;
                entry.Priority = CacheItemPriority.NeverRemove;
                return new List<BatchEvalReport>();
            })!;
            history.Add(report);
            if (history.Count > EvalHistoryMaxBatches)
            {
                history.RemoveRange(0, history.Count - EvalHistoryMaxBatches);
            }
        }
    }

    /// <summary>
    /// 把对 appsettings.json 的修改原子落盘并触发 IConfigurationRoot.Reload，
    /// IOptionsMonitor 随即把新值派发到所有消费方（配置 PUT 与语义路由 PUT 共用）。
    /// 临时文件 + File.Replace 保证读端（reload / 其他进程）不会读到半截 JSON。
    /// </summary>
    private static void PersistAppsettings(IConfiguration config, IWebHostEnvironment env, Action<JsonObject> mutate)
    {
        string appsettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        var root = JsonNode.Parse(File.ReadAllText(appsettingsPath))?.AsObject()
            ?? throw new InvalidOperationException("appsettings.json is unreadable; cannot persist config.");
        mutate(root);

        string directory = Path.GetDirectoryName(appsettingsPath) ?? env.ContentRootPath;
        string tempPath = Path.Combine(directory, $".appsettings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            int attempts = 0;
            while (true)
            {
                try
                {
                    if (File.Exists(appsettingsPath))
                        File.Replace(tempPath, appsettingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempPath, appsettingsPath);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    attempts++;
                    if (attempts >= 10) throw;
                    Thread.Sleep(10 * attempts);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        ((IConfigurationRoot)config).Reload();
    }

    private static readonly string[] ValidWindows = { "1h", "7h", "24h", "7d", "15d", "30d", "all" };

    private static string NormalizeWindow(string? window)
    {
        if (string.IsNullOrEmpty(window)) return "24h";
        string w = window.ToLowerInvariant();
        return Array.IndexOf(ValidWindows, w) >= 0 ? w : "24h";
    }

    /// <summary>
    /// 计算指定窗口的多维度统计汇总。窗口超出保留期时仍返回保留期内的聚合（前端据 WindowHours/RetentionHours 提示）。
    /// </summary>
    private static object ComputeWindowSummary(IRequestAuditStore auditStore, RouterOptions options, string window)
    {
        DateTime to = DateTime.UtcNow;
        DateTime from;
        int? windowHours = null;

        if (window == "all")
        {
            from = DateTime.MinValue; // timestamp >= '0001-...' 等价无下界
        }
        else
        {
            TimeSpan span = window switch
            {
                "1h" => TimeSpan.FromHours(1),
                "7h" => TimeSpan.FromHours(7),
                "24h" => TimeSpan.FromHours(24),
                "7d" => TimeSpan.FromDays(7),
                "15d" => TimeSpan.FromDays(15),
                "30d" => TimeSpan.FromDays(30),
                _ => TimeSpan.FromHours(24)
            };
            from = to - span;
            windowHours = (int)span.TotalHours;
        }

        var agg = auditStore.GetAggregateStats(from, to);
        long cacheDenom = agg.CachedInputTokens + agg.UncachedInputTokens;

        return new
        {
            Window = window,
            WindowHours = windowHours,
            RetentionHours = options.Routing.AuditRetentionHours,
            FromUtc = from == DateTime.MinValue ? (DateTime?)null : from,
            ToUtc = to,
            TotalRequests = agg.TotalRequests,
            Failures = agg.Failures,
            ErrorRatePercent = agg.TotalRequests > 0 ? Math.Round(agg.Failures * 100.0 / agg.TotalRequests, 2) : 0.0,
            InputTokens = agg.InputTokens,
            OutputTokens = agg.OutputTokens,
            CachedInputTokens = agg.CachedInputTokens,
            CacheWriteInputTokens = agg.CacheWriteInputTokens,
            UncachedInputTokens = agg.UncachedInputTokens,
            CacheHitRatePercent = cacheDenom > 0 ? Math.Round(agg.CachedInputTokens * 100.0 / cacheDenom, 2) : 0.0,
            AvgLatencyMs = agg.SuccessLatencySamples > 0 ? Math.Round((double)agg.SuccessLatencySumMs / agg.SuccessLatencySamples, 1) : 0.0,
            TotalCost = Math.Round(agg.TotalCost, 6)
        };
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
            r.FusionRole,
            r.RequestContent
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
