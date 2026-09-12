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
/// DashboardHandler 的私有辅助成员（#4 模块化：从主文件拆出的分部类）——
/// 租户用量视图、审计过滤与 CSV、配置应用与 Diff、评测数据集、窗口汇总与指标计算。
/// </summary>
public static partial class DashboardHandler
{
    /// <summary>租户用量视图（不含 KeyHash）。</summary>
    private sealed record TenantUsage(
        string KeyId,
        string KeyPrefix,
        string TenantName,
        decimal DailyBudgetUsd,
        decimal DailySpendUsd,
        decimal RemainingBudgetUsd,
        double QuotaUtilization,
        int DailyRequestCount,
        int MaxQps,
        bool Enabled,
        DateTime CreatedAt);

    private static TenantUsage TenantUsageDto(ClientKeyInfo key) => new(
        key.KeyId,
        key.KeyPrefix,
        key.TenantName,
        key.DailyBudgetUsd,
        key.DailySpendUsd,
        key.DailyBudgetUsd > 0m ? Math.Max(0m, key.DailyBudgetUsd - key.DailySpendUsd) : 0m,
        key.DailyBudgetUsd > 0m
            ? Math.Round(Math.Min(100.0, (double)(key.DailySpendUsd / key.DailyBudgetUsd) * 100.0), 2)
            : 0.0,
        key.DailyRequestCount,
        key.MaxQps,
        key.Enabled,
        key.CreatedAt);

    /// <summary>解析 UTC ISO 时间戳（审计分析端点参数）；空串返回 false。</summary>
    private static bool TryParseUtcTimestamp(string? value, out DateTime utc)
    {
        utc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>from/to 同时可解析且 from &gt;= to 时视为反选时间范围（与 analysis 端点口径一致）。</summary>
    private static bool IsInvertedTimeRange(string? from, string? to) =>
        DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fromUtc)
        && DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var toUtc)
        && fromUtc >= toUtc;

    /// <summary>status 为空或属于识别集合（success/error/200/429/500）才放行；未知值 400 而非静默忽略过滤。</summary>
    private static bool IsKnownAuditStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status is "200" or "429" or "500";
    }

    /// <summary>
    /// 审计日志统一筛选：model/tier/status/minLatency 为原有语义；
    /// q = RequestId/TraceId 子串匹配（不区分大小写）；from/to = UTC ISO 时间下界/上界。
    /// </summary>
    private static IEnumerable<RequestAuditRecord> ApplyAuditFilters(
        IEnumerable<RequestAuditRecord> source,
        string? model, string? tier, string? status, long? minLatency, string? q, string? from, string? to)
    {
        if (!string.IsNullOrWhiteSpace(model))
            source = source.Where(r => r.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(tier) && Enum.TryParse<ModelTier>(tier, ignoreCase: true, out var targetTier))
            source = source.Where(r => r.RoutedTier == targetTier);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("success", StringComparison.OrdinalIgnoreCase) || status == "200")
                source = source.Where(r => r.Success);
            else if (status.Equals("error", StringComparison.OrdinalIgnoreCase) || status == "429" || status == "500")
                source = source.Where(r => !r.Success);
        }

        if (minLatency.HasValue && minLatency.Value > 0)
            source = source.Where(r => r.LatencyMs >= minLatency.Value);

        if (!string.IsNullOrWhiteSpace(q))
            source = source.Where(r => (r.RequestId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                     || (r.TraceId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        if (DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fromUtc))
            source = source.Where(r => r.Timestamp >= fromUtc);
        if (DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var toUtc))
            source = source.Where(r => r.Timestamp <= toUtc);

        return source;
    }

    /// <summary>宽松解析 JSON 数组文本；失败时原样返回字符串（配置审计 Summary 兼容展示）。</summary>
    private static object TryParseJsonArray(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonArray();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }

    public record SandboxRouteRequest(string Prompt);

    public record CircuitOverrideRequest(string TargetState);
    public record CreateClientKeyRequest(string TenantName, decimal? DailyBudgetUsd, int? MaxQps);
    public record UpdateClientKeyRequest(bool? Enabled, decimal? DailyBudgetUsd, int? MaxQps);

    /// <summary>
    /// 系统配置更新请求。全部字段可空：null = 不修改。属性式（非位置 record）以便 40+ 字段可维护。
    /// 仅暴露热生效项；启动快照类配置（TokenEstimator 模式、OTLP 端点、Metrics 端点等）不进 UI。
    /// </summary>
    public sealed record UpdateSystemConfigRequest
    {
        public string? ExpectedVersion { get; init; }

        // ① 基础路由
        public bool? EnableRuleClassifier { get; init; }
        public bool? EnableSemanticRouter { get; init; }
        public bool? EnableSessionAffinity { get; init; }
        public bool? EnableLatencyAware { get; init; }
        public bool? EnableLoadBalance { get; init; }
        public bool? EnableKalmanLoadBalance { get; init; }
        public bool? EnableCapabilityFilter { get; init; }
        public string? DefaultTier { get; init; }

        // ② 可靠性与预算
        public bool? EnableFailover { get; init; }
        public int? FailoverFailureThreshold { get; init; }
        public int? FailoverCooldownSeconds { get; init; }
        public int? FailoverGlobalTimeoutSeconds { get; init; }
        public int? StreamFirstTokenTimeoutMs { get; init; }
        public int? StreamHedgeDelayMs { get; init; }
        public bool? EnableBudgetGuard { get; init; }
        public bool? EnableHealthProbe { get; init; }
        public decimal? DailyBudgetUsd { get; init; }
        public string? EnforceOnExhausted { get; init; }

        // ③ 学习与优化
        public bool? EnableThompsonSampling { get; init; }
        public bool? EnableContextualBandit { get; init; }
        public double? ExplorationEpsilon { get; init; }
        public long? ExplorationStarvedN { get; init; }
        public bool? EnableResponseCache { get; init; }
        public int? ResponseCacheTtlSeconds { get; init; }
        public int? ResponseCacheMaxEntries { get; init; }
        public bool? EnableSemanticCache { get; init; }
        public double? SemanticCacheSimilarityThreshold { get; init; }
        public int? SemanticCacheTtlMinutes { get; init; }
        public bool? EnableCascadeUpgrade { get; init; }
        public double? CascadeUpgradeSampleRate { get; init; }
        public bool? EnableRegenerateFeedback { get; init; }
        public bool? EnableQualityJudge { get; init; }
        public double? QualityJudgeSampleRate { get; init; }
        public string? QualityJudgeModel { get; init; }

        // ④ 合规与安全
        public bool? EnablePiiAnonymization { get; init; }
        public bool? EnableDataSovereignty { get; init; }
        public bool? EnableContentModeration { get; init; }
        public double? ModerationSampleRate { get; init; }
        public double? ModerationThreshold { get; init; }
        public bool? EnableStreamingComplianceFilter { get; init; }
        public bool? EnablePersonaDriftProtection { get; init; }
        public bool? EnablePromptCompression { get; init; }

        // ⑤ 高级编排
        public bool? EnableFusionRouter { get; init; }
        public string? FusionRouterMinComplexity { get; init; }
        public bool? EnableFusionMode { get; init; }
        public bool? EnableByzantineConsensus { get; init; }
        public bool? EnableJsonAstAutoRepair { get; init; }
        public int? FusionRouterPanelSize { get; init; }
        public bool? EnableDynamicFusionPanelSize { get; init; }
        public int? FusionRouterMinPanelSize { get; init; }
        public bool? EnableFusionDiversity { get; init; }
        public string? FusionRouterAnalystModel { get; init; }
        public string? FusionRouterAnalystPrompt { get; init; }
        public string? FusionRouterOuterModel { get; init; }
        public int? FusionRouterMaxOutputTokens { get; init; }
        public double? FusionRouterTemperature { get; init; }
        public double? FusionRouterPanelTemperature { get; init; }
        public int? FusionRouterPanelTimeoutSeconds { get; init; }
        public int? FusionMaxParallel { get; init; }
        public int? FusionHedgeDelayMs { get; init; }

        // ⑥ 观测
        public bool? EnableDistributedTracing { get; init; }
        public bool? AuditStoreRequestContent { get; init; }
        public int? AuditRetentionHours { get; init; }
        public string? AlertWebhookUrl { get; init; }
        public int? AlertWebhookIntervalSeconds { get; init; }
    }

    /// <summary>
    /// 把 PUT 请求的字段应用到配置克隆上，写入条件与落盘 JsonObject 的分支一一对应，
    /// 确保校验器看到的"候选配置"与实际持久化的内容一致。
    /// </summary>
    private static void ApplyRequestToOptions(RouterOptions candidate, UpdateSystemConfigRequest req)
    {
        var routing = candidate.Routing;

        // ① 基础路由
        if (req.EnableRuleClassifier is not null) routing.EnableRuleClassifier = req.EnableRuleClassifier.Value;
        if (req.EnableSemanticRouter is not null) routing.EnableSemanticRouter = req.EnableSemanticRouter.Value;
        if (req.EnableSessionAffinity is not null) routing.EnableSessionAffinity = req.EnableSessionAffinity.Value;
        if (req.EnableLatencyAware is not null) routing.EnableLatencyAware = req.EnableLatencyAware.Value;
        if (req.EnableLoadBalance is not null) routing.EnableLoadBalance = req.EnableLoadBalance.Value;
        if (req.EnableKalmanLoadBalance is not null) routing.EnableKalmanLoadBalance = req.EnableKalmanLoadBalance.Value;
        if (req.EnableCapabilityFilter is not null) routing.EnableCapabilityFilter = req.EnableCapabilityFilter.Value;
        if (!string.IsNullOrEmpty(req.DefaultTier) && Enum.TryParse<ModelTier>(req.DefaultTier, ignoreCase: true, out var tier))
        {
            routing.DefaultTier = tier;
        }

        // ② 可靠性与预算
        if (req.EnableFailover is not null) routing.EnableFailover = req.EnableFailover.Value;
        if (req.FailoverFailureThreshold is > 0) routing.FailoverFailureThreshold = req.FailoverFailureThreshold.Value;
        if (req.FailoverCooldownSeconds is > 0) routing.FailoverCooldownSeconds = req.FailoverCooldownSeconds.Value;
        if (req.FailoverGlobalTimeoutSeconds is >= 0) routing.FailoverGlobalTimeoutSeconds = req.FailoverGlobalTimeoutSeconds.Value;
        if (req.StreamFirstTokenTimeoutMs is >= 0) routing.StreamFirstTokenTimeoutMs = req.StreamFirstTokenTimeoutMs.Value;
        if (req.StreamHedgeDelayMs is >= 0) routing.StreamHedgeDelayMs = req.StreamHedgeDelayMs.Value;
        if (req.EnableBudgetGuard is not null) routing.EnableBudgetGuard = req.EnableBudgetGuard.Value;
        if (req.EnableHealthProbe is not null) routing.EnableHealthProbe = req.EnableHealthProbe.Value;
        if (req.DailyBudgetUsd is >= 0) candidate.Budget.DailyBudgetUsd = req.DailyBudgetUsd.Value;
        if (!string.IsNullOrEmpty(req.EnforceOnExhausted) && Enum.TryParse<BudgetExhaustionMode>(req.EnforceOnExhausted, ignoreCase: true, out var behavior))
        {
            candidate.Budget.EnforceOnExhausted = behavior;
        }

        // ③ 学习与优化
        if (req.EnableThompsonSampling is not null) routing.EnableThompsonSampling = req.EnableThompsonSampling.Value;
        if (req.EnableContextualBandit is not null) routing.EnableContextualBandit = req.EnableContextualBandit.Value;
        if (req.ExplorationEpsilon is not null) routing.ExplorationEpsilon = req.ExplorationEpsilon.Value;
        if (req.ExplorationStarvedN is not null) routing.ExplorationStarvedN = req.ExplorationStarvedN.Value;
        if (req.EnableResponseCache is not null) routing.EnableResponseCache = req.EnableResponseCache.Value;
        if (req.ResponseCacheTtlSeconds is > 0) routing.ResponseCacheTtlSeconds = req.ResponseCacheTtlSeconds.Value;
        if (req.ResponseCacheMaxEntries is > 0) routing.ResponseCacheMaxEntries = req.ResponseCacheMaxEntries.Value;
        if (req.EnableSemanticCache is not null) routing.EnableSemanticCache = req.EnableSemanticCache.Value;
        if (req.SemanticCacheSimilarityThreshold is > 0 and <= 1) routing.SemanticCacheSimilarityThreshold = (float)req.SemanticCacheSimilarityThreshold.Value;
        if (req.SemanticCacheTtlMinutes is > 0) routing.SemanticCacheTtlMinutes = req.SemanticCacheTtlMinutes.Value;
        if (req.EnableCascadeUpgrade is not null) routing.EnableCascadeUpgrade = req.EnableCascadeUpgrade.Value;
        if (req.CascadeUpgradeSampleRate is > 0 and <= 1) routing.CascadeUpgradeSampleRate = req.CascadeUpgradeSampleRate.Value;
        if (req.EnableRegenerateFeedback is not null) routing.EnableRegenerateFeedback = req.EnableRegenerateFeedback.Value;
        if (req.EnableQualityJudge is not null) routing.EnableQualityJudge = req.EnableQualityJudge.Value;
        if (req.QualityJudgeSampleRate is not null) routing.QualityJudgeSampleRate = req.QualityJudgeSampleRate.Value;
        if (req.QualityJudgeModel is not null) routing.QualityJudgeModel = req.QualityJudgeModel.Trim();

        // ④ 合规与安全
        if (req.EnablePiiAnonymization is not null) routing.EnablePiiAnonymization = req.EnablePiiAnonymization.Value;
        if (req.EnableDataSovereignty is not null) routing.EnableDataSovereignty = req.EnableDataSovereignty.Value;
        if (req.EnableContentModeration is not null) routing.EnableContentModeration = req.EnableContentModeration.Value;
        if (req.ModerationSampleRate is > 0 and <= 1) routing.ModerationSampleRate = req.ModerationSampleRate.Value;
        if (req.ModerationThreshold is > 0 and <= 1) routing.ModerationThreshold = req.ModerationThreshold.Value;
        if (req.EnableStreamingComplianceFilter is not null) routing.EnableStreamingComplianceFilter = req.EnableStreamingComplianceFilter.Value;
        if (req.EnablePersonaDriftProtection is not null) routing.EnablePersonaDriftProtection = req.EnablePersonaDriftProtection.Value;
        if (req.EnablePromptCompression is not null) routing.EnablePromptCompression = req.EnablePromptCompression.Value;

        // ⑤ 高级编排
        if (req.EnableFusionRouter is not null) routing.EnableFusionRouter = req.EnableFusionRouter.Value;
        if (!string.IsNullOrEmpty(req.FusionRouterMinComplexity) && Enum.TryParse<OptiRouter.Routing.RequestComplexity>(req.FusionRouterMinComplexity, ignoreCase: true, out var complexity))
        {
            routing.FusionRouterMinComplexity = complexity;
        }
        if (req.EnableFusionMode is not null) routing.EnableFusionMode = req.EnableFusionMode.Value;
        if (req.EnableByzantineConsensus is not null) routing.EnableByzantineConsensus = req.EnableByzantineConsensus.Value;
        if (req.EnableJsonAstAutoRepair is not null) routing.EnableJsonAstAutoRepair = req.EnableJsonAstAutoRepair.Value;
        if (req.FusionRouterPanelSize is >= 2 and <= 5) routing.FusionRouterPanelSize = req.FusionRouterPanelSize.Value;
        if (req.EnableDynamicFusionPanelSize is not null) routing.EnableDynamicFusionPanelSize = req.EnableDynamicFusionPanelSize.Value;
        if (req.FusionRouterMinPanelSize is >= 2 and <= 5) routing.FusionRouterMinPanelSize = req.FusionRouterMinPanelSize.Value;
        if (req.EnableFusionDiversity is not null) routing.EnableFusionDiversity = req.EnableFusionDiversity.Value;
        // 模型名/提示词：请求非 null 即写入（空串 = 清除回落默认主候选）。
        if (req.FusionRouterAnalystModel is not null) routing.FusionRouterAnalystModel = req.FusionRouterAnalystModel.Trim();
        if (req.FusionRouterAnalystPrompt is not null) routing.FusionRouterAnalystPrompt = req.FusionRouterAnalystPrompt.Trim();
        if (req.FusionRouterOuterModel is not null) routing.FusionRouterOuterModel = req.FusionRouterOuterModel.Trim();
        if (req.FusionRouterMaxOutputTokens is > 0) routing.FusionRouterMaxOutputTokens = req.FusionRouterMaxOutputTokens.Value;
        if (req.FusionRouterTemperature is >= 0 and <= 2) routing.FusionRouterTemperature = req.FusionRouterTemperature.Value;
        if (req.FusionRouterPanelTemperature is >= 0 and <= 2) routing.FusionRouterPanelTemperature = req.FusionRouterPanelTemperature.Value;
        if (req.FusionRouterPanelTimeoutSeconds is >= 0) routing.FusionRouterPanelTimeoutSeconds = req.FusionRouterPanelTimeoutSeconds.Value;
        if (req.FusionMaxParallel is >= 2 and <= 5) routing.FusionMaxParallel = req.FusionMaxParallel.Value;
        if (req.FusionHedgeDelayMs is >= 0) routing.FusionHedgeDelayMs = req.FusionHedgeDelayMs.Value;

        // ⑥ 观测
        if (req.EnableDistributedTracing is not null) routing.EnableDistributedTracing = req.EnableDistributedTracing.Value;
        if (req.AuditStoreRequestContent is not null) routing.AuditStoreRequestContent = req.AuditStoreRequestContent.Value;
        if (req.AuditRetentionHours is >= 0) routing.AuditRetentionHours = req.AuditRetentionHours.Value;
        // Webhook URL：请求非 null 即写入（空串 = 禁用推送，仅保留 Dashboard/历史展示）。
        if (req.AlertWebhookUrl is not null) routing.AlertWebhookUrl = req.AlertWebhookUrl.Trim();
        if (req.AlertWebhookIntervalSeconds is >= 5) routing.AlertWebhookIntervalSeconds = req.AlertWebhookIntervalSeconds.Value;
    }

    public record EvalRunRequest(List<EvalCaseRequest>? Cases);

    public record EvalCaseRequest(
        string? Id,
        string? Question,
        string? ExpectedAnswer,
        string? Category = null,
        long? MaxLatencyThresholdMs = null);

    public record EvalCompareRequest(string BaselineBatchId, string CandidateBatchId);

    public record UpdateSemanticRoutesRequest(List<SemanticRouteUpsertRequest>? Routes, string? ExpectedVersion = null);

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

    /// <summary>评测批次历史上限（与存储层裁剪保持一致）。</summary>
    private const int EvalHistoryMaxBatches = 10;

    private static List<BatchEvalReport> GetEvalHistory(AppConfigDbStore store)
    {
        var result = new List<BatchEvalReport>();
        foreach (var (_, _, json) in store.LoadEvalBatches())
        {
            try
            {
                var report = JsonSerializer.Deserialize<BatchEvalReport>(json, AppConfigDbStore.JsonOptions);
                if (report is not null)
                    result.Add(report);
            }
            catch (JsonException)
            {
                // 单批损坏跳过，不阻断其余批次（与模型配置容错语义一致）。
            }
        }
        return result;
    }

    private static void RecordEvalBatch(AppConfigDbStore store, BatchEvalReport report)
    {
        string json = JsonSerializer.Serialize(report, AppConfigDbStore.JsonOptions);
        store.SaveEvalBatch(
            report.BatchId,
            report.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            json,
            EvalHistoryMaxBatches);
    }

    /// <summary>
    /// 把路由/预算配置变更持久化到配置库并触发热重载。
    /// 变更以"当前 DB 文档 + 请求字段"合并后整体写回（与旧 appsettings.json 落盘语义一致）。
    /// 写入成功后计算新旧文档 key 级 diff 并记入配置变更审计（config_change_history）。
    /// </summary>
    private static bool TryPersistRoutingDocuments(
        IConfigurationRoot configRoot,
        AppConfigDbStore store,
        string? expectedVersion,
        string actor,
        Action<JsonObject> mutate,
        out string version)
    {
        static JsonObject LoadDoc(string? json)
        {
            return string.IsNullOrWhiteSpace(json)
                ? new JsonObject()
                : (JsonNode.Parse(json) as JsonObject ?? new JsonObject());
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = store.LoadRoutingBudgetSnapshot();
            if (expectedVersion is not null
                && !string.Equals(expectedVersion, snapshot.Version, StringComparison.Ordinal))
            {
                version = snapshot.Version;
                return false;
            }

            var root = new JsonObject
            {
                ["OptiRouter"] = new JsonObject
                {
                    ["Routing"] = LoadDoc(snapshot.RoutingJson),
                    ["Budget"] = LoadDoc(snapshot.BudgetJson)
                }
            };

            mutate(root);

            var optiRouter = root["OptiRouter"] as JsonObject;
            string routingJson = (optiRouter?["Routing"] as JsonObject ?? new JsonObject()).ToJsonString();
            string budgetJson = (optiRouter?["Budget"] as JsonObject ?? new JsonObject()).ToJsonString();
            if (!store.TrySaveRoutingBudgetDocuments(
                    snapshot.Version,
                    routingJson,
                    budgetJson,
                    out version))
            {
                if (expectedVersion is not null)
                    return false;
                continue;
            }

            // 变更审计：对比落库前后的 key 级差异（上限 50 条，防整表替换撑爆 summary）。
            var diff = BuildConfigDiff(snapshot.RoutingJson, snapshot.BudgetJson, routingJson, budgetJson);
            if (diff.Count > 0)
            {
                store.AppendConfigChange(actor, diff.ToJsonString());
            }

            // Reload 同步扇出 IOptionsMonitor 回调（RouterEngine/ModelClientProvider 等热生效）。
            configRoot.Reload();
            return true;
        }

        version = store.LoadRoutingBudgetSnapshot().Version;
        return false;
    }

    /// <summary>对比新旧路由/预算文档顶层 key，产出 [{key, from, to}] 差异数组（from/to 为 JSON 值文本）。</summary>
    private static JsonArray BuildConfigDiff(string? oldRouting, string? oldBudget, string? newRouting, string? newBudget)
    {
        static Dictionary<string, string?> Flatten(string? routing, string? budget)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (prefix, json) in new[] { ("Routing:", routing), ("Budget:", budget) })
            {
                if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject obj)
                    continue;
                foreach (var (key, value) in obj)
                {
                    map[prefix + key] = value?.ToJsonString();
                }
            }
            return map;
        }

        var oldMap = Flatten(oldRouting, oldBudget);
        var newMap = Flatten(newRouting, newBudget);
        var diff = new JsonArray();
        const int maxEntries = 50;
        foreach (var key in oldMap.Keys.Union(newMap.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            string? oldValue = oldMap.TryGetValue(key, out var v) ? v : null;
            string? newValue = newMap.TryGetValue(key, out var w) ? w : null;
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                continue;
            diff.Add(new JsonObject { ["key"] = key, ["from"] = oldValue, ["to"] = newValue });
            if (diff.Count >= maxEntries)
                break;
        }
        return diff;
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
                // 白名单投影：RoutingOptions 含 ModerationApiKey / MetricsApiKey / MeshRedisConnectionString
                // 等密钥类字段，不得整包下发浏览器（与 GET /api/dashboard/config 的字段白名单策略一致）。
                // 字段集与前端 ApiService.RoutingPolicyInfo 保持一一对应。
                RoutingPolicy = new
                {
                    options.Routing.EnableFailover,
                    options.Routing.EnableBudgetGuard,
                    options.Routing.EnableRuleClassifier,
                    options.Routing.EnableLatencyAware,
                    options.Routing.EnableSemanticRouter,
                    options.Routing.EnableMultiDimensionalRouting,
                    options.Routing.EnableThompsonSampling
                },
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
