using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiRouter.Endpoints;

/// <summary>
/// 提供可视化配置和健康状态监控 Dashboard 页面及 API 接口。
/// </summary>
public static partial class DashboardHandler
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
                // 归档只含已跨天的快照，需并入当日实时累计，趋势图才能显示"今天"。
                // 元组 Item1/Item2 是公共字段、STJ 不序列化（直接 Json 会输出 [{}]），
                // 必须投影为 {date, amount}，且 date 为 yyyy-MM-dd（blazor.js 以 date+'T00:00:00Z' 解析）。
                DateTime today = DateTime.UtcNow.Date;
                var points = store.GetDailyHistory(days)
                    .Where(h => h.Date != today)
                    .Select(h => new { date = h.Date.ToString("yyyy-MM-dd"), amount = h.Amount })
                    .ToList();
                points.Add(new { date = today.ToString("yyyy-MM-dd"), amount = store.GetDaily(today) });
                return Results.Json(points);
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

        // 3b. Learning State Reset：Thompson 与 Contextual Bandit 状态全部回落初始先验
        //     （调参实验或数据污染后从零学习；含持久化回落）。
        endpoints.MapPost("/api/dashboard/learning/reset", (ThompsonStateStore tsStore, ContextualBanditState banditState, IMemoryCache cache) =>
        {
            int thompsonCleared = tsStore.ResetAll();
            int banditCleared = banditState.ResetAll();
            cache.Remove("dashboard:learning");
            return Results.Ok(new
            {
                message = "Learning state reset to uniform prior.",
                thompsonCleared,
                banditCleared
            });
        });

        // 3c. Learning State CSV Export
        endpoints.MapGet("/api/dashboard/learning/export", (HttpContext httpContext, ThompsonStateStore tsStore) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("model,alpha,beta,mean_reward,samples,last_update_utc");
            foreach (var s in tsStore.GetSnapshot())
            {
                sb.AppendLine(string.Join(',',
                    CsvEscape(s.Model),
                    s.Alpha.ToString("F6", CultureInfo.InvariantCulture),
                    s.Beta.ToString("F6", CultureInfo.InvariantCulture),
                    s.Mean.ToString("F6", CultureInfo.InvariantCulture),
                    s.N,
                    s.LastUpdateUtc == DateTimeOffset.MinValue ? string.Empty : s.LastUpdateUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            }
            httpContext.Response.Headers.ContentDisposition = "attachment; filename=\"learning-state-export.csv\"";
            return Results.Text("\uFEFF" + sb.ToString(), "text/csv", System.Text.Encoding.UTF8);
        });

        // 3d. token 估算校准诊断：校准比率 EMA（actual/estimated）与采样数。
        //     验证 CalibratingTokenEstimator 收敛情况——比率长期偏离 1.0 说明内层估算
        //     偏差未被拉平（如修复前的平方根收敛缺陷），预算预留会系统性失准。
        endpoints.MapGet("/api/dashboard/diagnostics/calibration", (
            Routing.CalibratingTokenEstimator estimator,
            IOptionsMonitor<RouterOptions> options) =>
        {
            return Results.Json(new
            {
                mode = options.CurrentValue.Routing.TokenEstimation.ToString(),
                ratio = Math.Round(estimator.CurrentRatio, 4),
                observations = estimator.Observations
            });
        });

        // 3d. Alert History：告警出现/恢复事件（进程内环形缓冲，重启清空）
        endpoints.MapGet("/api/dashboard/alerts/history", (AlertHistory history)
            => Results.Ok(history.GetRecent(100)));


        // 4. Request Audit Log API with Multi-Filter Support
        endpoints.MapGet("/api/dashboard/requests", (IRequestAuditStore auditStore, int limit = 50, int offset = 0, string? model = null, string? tier = null, string? status = null, long? minLatency = null, string? q = null, string? from = null, string? to = null) =>
        {
            if (IsInvertedTimeRange(from, to))
                return Results.BadRequest(new { error = "'from' must be earlier than 'to'." });
            if (!IsKnownAuditStatus(status))
                return Results.BadRequest(new { error = "Unknown status filter; expected success|error|200|429|500." });
            if (limit <= 0) limit = 50;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            var recent = auditStore.GetRecent(500);
            var filtered = ApplyAuditFilters(recent.AsEnumerable(), model, tier, status, minLatency, q, from, to);

            var totalCount = filtered.Count();
            var pageItems = filtered.Skip(offset).Take(limit).ToList();

            // 列表基于最近 500 条活动缓冲过滤：命中缓冲上限时 totalCount 只是下界，前端需提示。
            bool bufferLimited = recent.Count >= 500;
            return Results.Json(new { items = pageItems, totalCount, bufferLimited });
        });

        // 4a. Audit Log CSV Export（与列表接口同一套筛选；作用域同为最近 500 条活动缓冲）
        endpoints.MapGet("/api/dashboard/requests/export", async (HttpContext httpContext, IRequestAuditStore auditStore, string? model = null, string? tier = null, string? status = null, long? minLatency = null, string? q = null, string? from = null, string? to = null) =>
        {
            // 与列表端点同一套入口校验；本 lambda 直接写响应体，BadRequest 手写。
            if (IsInvertedTimeRange(from, to) || !IsKnownAuditStatus(status))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid export filter: 'from' must be earlier than 'to'; status must be success|error|200|429|500." });
                return;
            }
            var rows = ApplyAuditFilters(auditStore.GetRecent(500).AsEnumerable(), model, tier, status, minLatency, q, from, to).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("timestamp_utc,request_id,trace_id,model,tier,success,prompt_tokens,completion_tokens,cached_input_tokens,cache_write_input_tokens,cost_usd,latency_ms,ttft_ms,streaming,error_message");
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(',',
                    r.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CsvEscape(r.RequestId),
                    CsvEscape(r.TraceId),
                    CsvEscape(r.Model),
                    r.RoutedTier,
                    r.Success,
                    r.PromptTokens,
                    r.CompletionTokens,
                    r.CachedInputTokens,
                    r.CacheWriteInputTokens,
                    r.Cost.ToString("F6"),
                    r.LatencyMs,
                    r.TimeToFirstTokenMs.HasValue ? r.TimeToFirstTokenMs.Value.ToString() : string.Empty,
                    r.IsStreaming,
                    CsvEscape(r.ErrorMessage)));
            }
            // 带 BOM 便于 Excel 正确识别 UTF-8 中文。
            httpContext.Response.Headers.ContentDisposition = "attachment; filename=\"request-audit-export.csv\"";
            await httpContext.Response.WriteAsync("\uFEFF" + sb.ToString(), httpContext.RequestAborted);
        });

        // 4a-2. 审计分析：时间窗全量聚合报告（总览/分模型/分档/级联/Fusion/路由原因/日趋势）。
        // 与列表接口的"最近 500 条活动缓冲"不同，本端点经 GetByTimeRange 分页拉取全窗口，供策略调优闭环。
        endpoints.MapGet("/api/dashboard/audit/analysis", (AuditAnalysisService analyzer, string? from = null, string? to = null) =>
        {
            if (!TryParseUtcTimestamp(from, out DateTime fromUtc))
                return Results.BadRequest(new { error = "from 必须是 ISO 8601 时间（如 2026-08-20T00:00:00Z）。" });
            if (!TryParseUtcTimestamp(to, out DateTime toUtc))
                return Results.BadRequest(new { error = "to 必须是 ISO 8601 时间（如 2026-08-21T00:00:00Z）。" });
            if (fromUtc >= toUtc)
                return Results.BadRequest(new { error = "from 必须早于 to。" });

            return Results.Ok(analyzer.Analyze(fromUtc, toUtc));
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
                TargetTier = decision.ClassificationTargetTier?.ToString()
                    ?? (decision.Candidates.Count > 0 ? decision.Candidates[0].Tier.ToString() : ModelTier.Medium.ToString()),
                Reasons = decision.ReasonEvents.Select(r => new { PolicyName = r.Policy, Message = r.Detail }),
                EstimatedTokens = decision.EstimatedInputTokens,
                CandidateModels = decision.Candidates.Select(m => m.Name).ToList()
            });
        });

        // 6. Golden Dataset Offline Regression Runner API
        //    经 ProxyOrchestrator.SendAsync 走完整真实管线（路由→上游→计费→审计），
        //    取代旧桩实现（伪造固定回复，评测结果无意义）。Cases 为空时回落内置题库。
        //    注意：评测请求消耗真实生产日预算（最多 50 用例 × 真实计费），可能挤占正常业务配额。
        endpoints.MapPost("/api/dashboard/eval/run", async (
            HttpContext httpContext,
            ProxyOrchestrator orchestrator,
            AppConfigDbStore store,
            EvalRunRequest? req,
            CancellationToken requestAborted) =>
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
                (request, token) => orchestrator.SendAsync(request, token),
                ct: requestAborted);

            httpContext.Response.Headers["X-Eval-Consumes-Budget"] = "true";
            RecordEvalBatch(store, report);
            return Results.Ok(report);
        });

        // 6b. Eval Batch History API（SQLite 持久化，保留最近 10 批，重启不丢）
        endpoints.MapGet("/api/dashboard/eval/batches", (AppConfigDbStore store) =>
        {
            var batches = GetEvalHistory(store)
                .OrderByDescending(r => r.Timestamp)
                .ToList();
            return Results.Ok(batches);
        });

        // 6c. Paired A/B Compare API——复用 OfflineEvalRunner.Compare 按用例 ID 成对比较两个批次
        endpoints.MapPost("/api/dashboard/eval/compare", (AppConfigDbStore store, EvalCompareRequest req) =>
        {
            var history = GetEvalHistory(store);
            var baseline = history.FirstOrDefault(r => string.Equals(r.BatchId, req.BaselineBatchId, StringComparison.Ordinal));
            var candidate = history.FirstOrDefault(r => string.Equals(r.BatchId, req.CandidateBatchId, StringComparison.Ordinal));
            if (baseline is null || candidate is null)
            {
                return Results.NotFound(new { error = "批次不存在，请确认批次 ID 后重试。" });
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

        endpoints.MapGet("/api/dashboard/state/cache-affinity", (PromptCacheAffinityStore store, IOptionsMonitor<RouterOptions> options) =>
        {
            var entries = store.GetEntries();
            return Results.Ok(new
            {
                enabled = options.CurrentValue.Routing.EnablePromptCacheAffinity,
                totalCount = entries.Count,
                // 条目可达上万，只下发最近 50 条；指纹已是最短可辨前缀展示由前端截断
                items = entries.Take(50)
            });
        });

        endpoints.MapGet("/api/dashboard/state/response-cache", (MemoryResponseCache responseCache, IOptionsMonitor<RouterOptions> options) =>
        {
            var (hits, misses, sets, current, max, currentBytes, maxBytes) = responseCache.GetStats();
            long total = hits + misses;
            return Results.Ok(new
            {
                enabled = options.CurrentValue.Routing.EnableResponseCache,
                hits,
                misses,
                sets,
                currentEntries = current,
                maxEntries = max,
                currentBytes,
                maxBytes,
                hitRatePercent = total > 0 ? (double)hits / total * 100.0 : 0.0
            });
        });

        // 10c. Semantic Routes Management APIs——语义路由此前是唯一只能手改配置文件的路由策略。
        //     SemanticRouterPolicy 每请求从 context.Options 读取路由表，reload 后立即热生效。
        endpoints.MapGet("/api/dashboard/semantic-routes", (IOptionsMonitor<RouterOptions> options, AppConfigDbStore store) =>
        {
            var opt = options.CurrentValue.Routing;
            var routes = (opt.SemanticRoutes ?? new List<SemanticRouteOptions>())
                .Select(r => new { r.Name, r.Phrases, TargetTier = r.TargetTier.ToString() })
                .ToList();
            return Results.Ok(new
            {
                enabled = opt.EnableSemanticRouter,
                similarityThreshold = opt.SemanticSimilarityThreshold,
                routes,
                // 保存时作为 ExpectedVersion 回传，防止并发编辑静默覆盖。
                version = store.LoadRoutingBudgetSnapshot().Version
            });
        });

        endpoints.MapPut("/api/dashboard/semantic-routes", (
            IConfiguration config,
            AppConfigDbStore store,
            UpdateSemanticRoutesRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ExpectedVersion))
            {
                return Results.BadRequest(new { error = "ExpectedVersion is required. Reload the semantic routes before saving." });
            }

            var (routes, error) = BuildSemanticRoutes(req.Routes);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            if (!TryPersistRoutingDocuments((IConfigurationRoot)config, store, req.ExpectedVersion, "admin", root =>
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
            }, out var newVersion))
            {
                return Results.Conflict(new { error = "Configuration changed concurrently; retry the semantic route update." });
            }

            return Results.Ok(new
            {
                message = $"Semantic routes persisted ({routes.Count} rules) and hot-applied via reload.",
                version = newVersion
            });
        });

        // 7. GET System Config API（读 IOptionsMonitor.CurrentValue，反映 reload 后的真值）
        endpoints.MapGet("/api/dashboard/config", (IOptionsMonitor<RouterOptions> options, AppConfigDbStore store) =>
        {
            var opt = options.CurrentValue;
            var snapshot = store.LoadRoutingBudgetSnapshot();
            return Results.Ok(new
            {
                snapshot.Version,
                Routing = new
                {
                    Preset = opt.Routing.Preset,
                    // ① 基础路由
                    DefaultTier = opt.Routing.DefaultTier.ToString(),
                    opt.Routing.EnableRuleClassifier,
                    opt.Routing.EnableSemanticRouter,
                    opt.Routing.EnableSessionAffinity,
                    opt.Routing.EnableLatencyAware,
                    opt.Routing.EnableLoadBalance,
                    opt.Routing.EnableKalmanLoadBalance,
                    opt.Routing.EnableCapabilityFilter,
                    // ② 可靠性与预算
                    opt.Routing.EnableFailover,
                    opt.Routing.FailoverFailureThreshold,
                    opt.Routing.FailoverCooldownSeconds,
                    opt.Routing.FailoverGlobalTimeoutSeconds,
                    opt.Routing.StreamFirstTokenTimeoutMs,
                    opt.Routing.StreamHedgeDelayMs,
                    opt.Routing.EnableBudgetGuard,
                    opt.Routing.EnableHealthProbe,
                    // ③ 学习与优化
                    opt.Routing.EnableThompsonSampling,
                    opt.Routing.EnableContextualBandit,
                    opt.Routing.ExplorationEpsilon,
                    opt.Routing.ExplorationStarvedN,
                    opt.Routing.EnableResponseCache,
                    opt.Routing.ResponseCacheTtlSeconds,
                    opt.Routing.ResponseCacheMaxEntries,
                    opt.Routing.EnableSemanticCache,
                    opt.Routing.SemanticCacheSimilarityThreshold,
                    opt.Routing.SemanticCacheTtlMinutes,
                    opt.Routing.EnableCascadeUpgrade,
                    opt.Routing.CascadeUpgradeSampleRate,
                    opt.Routing.EnableRegenerateFeedback,
                    opt.Routing.EnableQualityJudge,
                    opt.Routing.QualityJudgeSampleRate,
                    QualityJudgeModel = opt.Routing.QualityJudgeModel,
                    // ④ 合规与安全
                    opt.Routing.EnablePiiAnonymization,
                    opt.Routing.EnableDataSovereignty,
                    opt.Routing.EnableContentModeration,
                    opt.Routing.ModerationSampleRate,
                    opt.Routing.ModerationThreshold,
                    opt.Routing.EnableStreamingComplianceFilter,
                    opt.Routing.EnablePersonaDriftProtection,
                    opt.Routing.EnablePromptCompression,
                    // ⑤ 高级编排
                    opt.Routing.EnableFusionRouter,
                    FusionRouterMinComplexity = opt.Routing.FusionRouterMinComplexity.ToString(),
                    opt.Routing.EnableFusionMode,
                    opt.Routing.EnableByzantineConsensus,
                    opt.Routing.EnableJsonAstAutoRepair,
                    opt.Routing.FusionRouterPanelSize,
                    opt.Routing.EnableDynamicFusionPanelSize,
                    opt.Routing.FusionRouterMinPanelSize,
                    opt.Routing.EnableFusionDiversity,
                    opt.Routing.FusionRouterAnalystModel,
                    opt.Routing.FusionRouterAnalystPrompt,
                    opt.Routing.FusionRouterOuterModel,
                    opt.Routing.FusionRouterMaxOutputTokens,
                    opt.Routing.FusionRouterTemperature,
                    opt.Routing.FusionRouterPanelTemperature,
                    opt.Routing.FusionRouterPanelTimeoutSeconds,
                    opt.Routing.FusionMaxParallel,
                    opt.Routing.FusionHedgeDelayMs,
                    // ⑥ 观测
                    opt.Routing.EnableDistributedTracing,
                    opt.Routing.AuditStoreRequestContent,
                    opt.Routing.AuditRetentionHours,
                    opt.Routing.AlertWebhookUrl,
                    opt.Routing.AlertWebhookIntervalSeconds
                },
                Budget = new
                {
                    opt.Budget.DailyBudgetUsd,
                    EnforceOnExhausted = opt.Budget.EnforceOnExhausted.ToString()
                }
            });
        });

        // 7b. GET Presets API：三档路由预设（供配置页一键填充表单；应用预设 = 显式 PUT 全部预设字段）。
        endpoints.MapGet("/api/dashboard/config/presets",
            () => Results.Ok(RoutingPreset.GetPresets()));

        // 7c. Config Change History：路由/预算配置每次落库的变更审计（谁在何时改了哪项，保留最近 200 条）。
        //     Summary 为 [{key, from, to}] 紧凑 JSON；解析失败时原样返回不阻断列表。
        endpoints.MapGet("/api/dashboard/config/history", (AppConfigDbStore store, int limit = 50) =>
        {
            if (limit <= 0 || limit > 200) limit = 50;
            return Results.Ok(store.LoadConfigChanges(limit).Select(c => new
            {
                c.Id,
                timestamp = c.Ts,
                c.Actor,
                changes = TryParseJsonArray(c.Summary)
            }));
        });

        // 8. PUT Update System Config API（持久化到 appsettings.json + 触发 IConfigurationRoot.Reload，
        //    IOptionsMonitor 自然派发到所有消费方；取代旧版 mutate IOptions.Value 的非持久写法，
        //    后者被 models-config.json 写入触发的整体 reload 覆盖、且重启丢失）。
        endpoints.MapPut("/api/dashboard/config", (
            IConfiguration config,
            AppConfigDbStore store,
            IOptionsMonitor<RouterOptions> optionsMonitor,
            UpdateSystemConfigRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ExpectedVersion))
            {
                return Results.BadRequest(new { error = "ExpectedVersion is required. Reload the configuration before saving." });
            }

            // 落库前把变更应用到"当前配置的克隆"并复用启动校验器（RouterOptionsValidator）：
            // 坏配置一旦落库，reload 会被 IOptionsMonitor 静默拒绝（表面保存成功、实际未生效），
            // 更糟的是重启时 ValidateOnStart 直接失败导致进程起不来。
            // 克隆 = 从组合配置重新绑定（与启动同源）+ 用权威模型列表校正
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

            if (!TryPersistRoutingDocuments((IConfigurationRoot)config, store, req.ExpectedVersion, "admin", root =>
            {
                var optiRouter = (root["OptiRouter"] as JsonObject) ?? (JsonObject)(root["OptiRouter"] = new JsonObject());
                var routing = (optiRouter["Routing"] as JsonObject) ?? (JsonObject)(optiRouter["Routing"] = new JsonObject());
                var budget = (optiRouter["Budget"] as JsonObject) ?? (JsonObject)(optiRouter["Budget"] = new JsonObject());

                // 写入条件与 ApplyRequestToOptions 一一对应（null = 不改）
                // ① 基础路由
                if (req.EnableRuleClassifier is not null) routing["EnableRuleClassifier"] = req.EnableRuleClassifier.Value;
                if (req.EnableSemanticRouter is not null) routing["EnableSemanticRouter"] = req.EnableSemanticRouter.Value;
                if (req.EnableSessionAffinity is not null) routing["EnableSessionAffinity"] = req.EnableSessionAffinity.Value;
                if (req.EnableLatencyAware is not null) routing["EnableLatencyAware"] = req.EnableLatencyAware.Value;
                if (req.EnableLoadBalance is not null) routing["EnableLoadBalance"] = req.EnableLoadBalance.Value;
                if (req.EnableKalmanLoadBalance is not null) routing["EnableKalmanLoadBalance"] = req.EnableKalmanLoadBalance.Value;
                if (req.EnableCapabilityFilter is not null) routing["EnableCapabilityFilter"] = req.EnableCapabilityFilter.Value;
                if (!string.IsNullOrEmpty(req.DefaultTier) && Enum.TryParse<ModelTier>(req.DefaultTier, ignoreCase: true, out var tier)) routing["DefaultTier"] = tier.ToString();

                // ② 可靠性与预算
                if (req.EnableFailover is not null) routing["EnableFailover"] = req.EnableFailover.Value;
                if (req.FailoverFailureThreshold is > 0) routing["FailoverFailureThreshold"] = req.FailoverFailureThreshold.Value;
                if (req.FailoverCooldownSeconds is > 0) routing["FailoverCooldownSeconds"] = req.FailoverCooldownSeconds.Value;
                if (req.FailoverGlobalTimeoutSeconds is >= 0) routing["FailoverGlobalTimeoutSeconds"] = req.FailoverGlobalTimeoutSeconds.Value;
                if (req.StreamFirstTokenTimeoutMs is >= 0) routing["StreamFirstTokenTimeoutMs"] = req.StreamFirstTokenTimeoutMs.Value;
                if (req.StreamHedgeDelayMs is >= 0) routing["StreamHedgeDelayMs"] = req.StreamHedgeDelayMs.Value;
                if (req.EnableBudgetGuard is not null) routing["EnableBudgetGuard"] = req.EnableBudgetGuard.Value;
                if (req.EnableHealthProbe is not null) routing["EnableHealthProbe"] = req.EnableHealthProbe.Value;

                // ③ 学习与优化
                if (req.EnableThompsonSampling is not null) routing["EnableThompsonSampling"] = req.EnableThompsonSampling.Value;
                if (req.EnableContextualBandit is not null) routing["EnableContextualBandit"] = req.EnableContextualBandit.Value;
                if (req.ExplorationEpsilon is not null) routing["ExplorationEpsilon"] = req.ExplorationEpsilon.Value;
                if (req.ExplorationStarvedN is not null) routing["ExplorationStarvedN"] = req.ExplorationStarvedN.Value;
                if (req.EnableResponseCache is not null) routing["EnableResponseCache"] = req.EnableResponseCache.Value;
                if (req.ResponseCacheTtlSeconds is > 0) routing["ResponseCacheTtlSeconds"] = req.ResponseCacheTtlSeconds.Value;
                if (req.ResponseCacheMaxEntries is > 0) routing["ResponseCacheMaxEntries"] = req.ResponseCacheMaxEntries.Value;
                if (req.EnableSemanticCache is not null) routing["EnableSemanticCache"] = req.EnableSemanticCache.Value;
                if (req.SemanticCacheSimilarityThreshold is > 0 and <= 1) routing["SemanticCacheSimilarityThreshold"] = req.SemanticCacheSimilarityThreshold.Value;
                if (req.SemanticCacheTtlMinutes is > 0) routing["SemanticCacheTtlMinutes"] = req.SemanticCacheTtlMinutes.Value;
                if (req.EnableCascadeUpgrade is not null) routing["EnableCascadeUpgrade"] = req.EnableCascadeUpgrade.Value;
                if (req.CascadeUpgradeSampleRate is > 0 and <= 1) routing["CascadeUpgradeSampleRate"] = req.CascadeUpgradeSampleRate.Value;
                if (req.EnableRegenerateFeedback is not null) routing["EnableRegenerateFeedback"] = req.EnableRegenerateFeedback.Value;
                if (req.EnableQualityJudge is not null) routing["EnableQualityJudge"] = req.EnableQualityJudge.Value;
                if (req.QualityJudgeSampleRate is >= 0 and <= 1) routing["QualityJudgeSampleRate"] = req.QualityJudgeSampleRate.Value;
                if (req.QualityJudgeModel is not null) routing["QualityJudgeModel"] = req.QualityJudgeModel.Trim();

                // ④ 合规与安全
                if (req.EnablePiiAnonymization is not null) routing["EnablePiiAnonymization"] = req.EnablePiiAnonymization.Value;
                if (req.EnableDataSovereignty is not null) routing["EnableDataSovereignty"] = req.EnableDataSovereignty.Value;
                if (req.EnableContentModeration is not null) routing["EnableContentModeration"] = req.EnableContentModeration.Value;
                if (req.ModerationSampleRate is > 0 and <= 1) routing["ModerationSampleRate"] = req.ModerationSampleRate.Value;
                if (req.ModerationThreshold is > 0 and <= 1) routing["ModerationThreshold"] = req.ModerationThreshold.Value;
                if (req.EnableStreamingComplianceFilter is not null) routing["EnableStreamingComplianceFilter"] = req.EnableStreamingComplianceFilter.Value;
                if (req.EnablePersonaDriftProtection is not null) routing["EnablePersonaDriftProtection"] = req.EnablePersonaDriftProtection.Value;
                if (req.EnablePromptCompression is not null) routing["EnablePromptCompression"] = req.EnablePromptCompression.Value;

                // ⑤ 高级编排
                if (req.EnableFusionRouter is not null) routing["EnableFusionRouter"] = req.EnableFusionRouter.Value;
                if (!string.IsNullOrEmpty(req.FusionRouterMinComplexity) && Enum.TryParse<OptiRouter.Routing.RequestComplexity>(req.FusionRouterMinComplexity, ignoreCase: true, out var complexity)) routing["FusionRouterMinComplexity"] = complexity.ToString();
                if (req.EnableFusionMode is not null) routing["EnableFusionMode"] = req.EnableFusionMode.Value;
                if (req.EnableByzantineConsensus is not null) routing["EnableByzantineConsensus"] = req.EnableByzantineConsensus.Value;
                if (req.EnableJsonAstAutoRepair is not null) routing["EnableJsonAstAutoRepair"] = req.EnableJsonAstAutoRepair.Value;
                if (req.FusionRouterPanelSize is >= 2 and <= 5) routing["FusionRouterPanelSize"] = req.FusionRouterPanelSize.Value;
                if (req.EnableDynamicFusionPanelSize is not null) routing["EnableDynamicFusionPanelSize"] = req.EnableDynamicFusionPanelSize.Value;
                if (req.FusionRouterMinPanelSize is >= 2 and <= 5) routing["FusionRouterMinPanelSize"] = req.FusionRouterMinPanelSize.Value;
                if (req.EnableFusionDiversity is not null) routing["EnableFusionDiversity"] = req.EnableFusionDiversity.Value;
                if (req.FusionRouterAnalystModel is not null) routing["FusionRouterAnalystModel"] = req.FusionRouterAnalystModel.Trim();
                if (req.FusionRouterAnalystPrompt is not null) routing["FusionRouterAnalystPrompt"] = req.FusionRouterAnalystPrompt.Trim();
                if (req.FusionRouterOuterModel is not null) routing["FusionRouterOuterModel"] = req.FusionRouterOuterModel.Trim();
                if (req.FusionRouterMaxOutputTokens is > 0) routing["FusionRouterMaxOutputTokens"] = req.FusionRouterMaxOutputTokens.Value;
                if (req.FusionRouterTemperature is >= 0 and <= 2) routing["FusionRouterTemperature"] = req.FusionRouterTemperature.Value;
                if (req.FusionRouterPanelTemperature is >= 0 and <= 2) routing["FusionRouterPanelTemperature"] = req.FusionRouterPanelTemperature.Value;
                if (req.FusionRouterPanelTimeoutSeconds is >= 0) routing["FusionRouterPanelTimeoutSeconds"] = req.FusionRouterPanelTimeoutSeconds.Value;
                if (req.FusionMaxParallel is >= 2 and <= 5) routing["FusionMaxParallel"] = req.FusionMaxParallel.Value;
                if (req.FusionHedgeDelayMs is >= 0) routing["FusionHedgeDelayMs"] = req.FusionHedgeDelayMs.Value;

                // ⑥ 观测
                if (req.EnableDistributedTracing is not null) routing["EnableDistributedTracing"] = req.EnableDistributedTracing.Value;
                if (req.AuditStoreRequestContent is not null) routing["AuditStoreRequestContent"] = req.AuditStoreRequestContent.Value;
                if (req.AuditRetentionHours is >= 0) routing["AuditRetentionHours"] = req.AuditRetentionHours.Value;
                if (req.AlertWebhookUrl is not null) routing["AlertWebhookUrl"] = req.AlertWebhookUrl.Trim();
                if (req.AlertWebhookIntervalSeconds is >= 5) routing["AlertWebhookIntervalSeconds"] = req.AlertWebhookIntervalSeconds.Value;

                if (req.DailyBudgetUsd is >= 0) budget["DailyBudgetUsd"] = req.DailyBudgetUsd.Value;
                if (!string.IsNullOrEmpty(req.EnforceOnExhausted) && Enum.TryParse<BudgetExhaustionMode>(req.EnforceOnExhausted, ignoreCase: true, out var behavior))
                {
                    budget["EnforceOnExhausted"] = behavior.ToString();
                }
            }, out string version))
            {
                return Results.Conflict(new
                {
                    error = "Configuration changed since it was loaded. Reload before saving.",
                    currentVersion = version
                });
            }

            return Results.Ok(new
            {
                message = "System configuration persisted to the config database and hot-applied via reload.",
                version
            });
        });

        // 9. Circuit Breaker Override API
        endpoints.MapPost("/api/dashboard/circuits/{name}/override", (string name, ModelHealthTracker tracker, CircuitOverrideRequest req) =>
        {
            name = Uri.UnescapeDataString(name);
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

        // 11. Tenant usage & quota APIs（配额设置见上方 PUT /keys/{keyId}；此处提供用量查询与导出）
        endpoints.MapGet("/api/dashboard/keys/usage", (ClientKeyService keySvc) =>
        {
            var usages = keySvc.GetAllKeys().Select(TenantUsageDto);
            return Results.Ok(usages);
        });

        endpoints.MapGet("/api/dashboard/keys/{keyId}/usage", (string keyId, ClientKeyService keySvc) =>
        {
            var key = keySvc.GetAllKeys().FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (key is null) return Results.NotFound(new { error = $"Client key '{keyId}' not found." });
            return Results.Ok(TenantUsageDto(key));
        });

        endpoints.MapGet("/api/dashboard/keys/usage/export", (ClientKeyService keySvc) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("key_id,key_prefix,tenant_name,daily_budget_usd,daily_spend_usd,remaining_budget_usd,quota_utilization_pct,daily_request_count,max_qps,enabled,created_at_utc");
            foreach (var key in keySvc.GetAllKeys())
            {
                var dto = TenantUsageDto(key);
                sb.AppendLine(string.Join(',',
                    CsvEscape(dto.KeyId),
                    CsvEscape(dto.KeyPrefix),
                    CsvEscape(dto.TenantName),
                    dto.DailyBudgetUsd.ToString("F4"),
                    dto.DailySpendUsd.ToString("F4"),
                    dto.RemainingBudgetUsd.ToString("F4"),
                    dto.QuotaUtilization.ToString("F2"),
                    dto.DailyRequestCount,
                    dto.MaxQps,
                    dto.Enabled,
                    dto.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            }
            // 带 BOM 便于 Excel 正确识别 UTF-8 中文。
            return Results.Text("\uFEFF" + sb.ToString(), "text/csv", System.Text.Encoding.UTF8);
        });

        // 会话保活（Blazor Server 管理台专用）：页面加载后浏览器不再发 HTTP 请求，
        // Cookie 的 8h 滑动过期无请求可滑动，面板常开超 8h 必然掉登录（断线重连
        // negotiate 被 302 到 /login，重连横幅永久卡死）。前端 blazor.js 每 30 分钟
        // 带 Cookie 请求本端点触发续期。鉴权由管理端中间件按 /api/dashboard 前缀统一执行。
        endpoints.MapGet("/api/dashboard/session/ping", () => Results.NoContent());
    }

}
