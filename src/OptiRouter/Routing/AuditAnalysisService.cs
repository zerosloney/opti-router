using System.Globalization;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>审计分析报告（由 <see cref="AuditAnalysisService"/> 在指定时间窗内聚合生成）。</summary>
public sealed record AuditAnalysisReport(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalRequests,
    AuditAnalysisSummary Summary,
    IReadOnlyList<AuditModelStats> ByModel,
    IReadOnlyList<AuditProviderStats> ByProvider,
    IReadOnlyList<AuditTierStats> ByTier,
    AuditCascadeStats Cascade,
    AuditFusionStats Fusion,
    IReadOnlyList<AuditReasonStats> ByReason,
    IReadOnlyList<AuditDayStats> DailyTrend)
{
    /// <summary>
    /// 模型路由名 → 供应商（本报告聚合所用的完整解析结果：当前配置显式/推断 + 已删除模型墓碑）。
    /// 供前端"模型用量明细"供应商列与后端 ByProvider 同口径渲染，不再各自联表。
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderByModel { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>窗口总览：总量/成败/成本/Token/成功请求延迟分位。</summary>
public sealed record AuditAnalysisSummary(
    int Successes,
    int Failures,
    double SuccessRatePct,
    double TotalCostUsd,
    long PromptTokens,
    long CompletionTokens,
    long CachedInputTokens,
    double AvgLatencyMs,
    double P50LatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    int LatencySamples);

/// <summary>单模型聚合。</summary>
public sealed record AuditModelStats(
    string Model,
    int Requests,
    int Failures,
    double SuccessRatePct,
    double CostUsd,
    double AvgLatencyMs,
    double P95LatencyMs,
    long PromptTokens,
    long CompletionTokens,
    long CachedInputTokens);

/// <summary>
/// 单供应商聚合：模型路由名经 <see cref="ModelEndpointOptions.Provider"/> 映射归组；
/// 配置缺失或 Provider 为空的模型归入 <see cref="AuditAnalysisService.UnknownProvider"/> 桶。
/// </summary>
public sealed record AuditProviderStats(
    string Provider,
    int Requests,
    int Failures,
    double SuccessRatePct,
    long PromptTokens,
    long CachedInputTokens,
    long CompletionTokens,
    double CostUsd,
    int ModelCount);

/// <summary>单分档（routed_tier）聚合。</summary>
public sealed record AuditTierStats(
    string Tier,
    int Requests,
    int Failures,
    double SuccessRatePct,
    double CostUsd,
    double CostSharePct);

/// <summary>级联触发统计。</summary>
public sealed record AuditCascadeStats(
    int Triggered,
    double TriggerRatePct,
    IReadOnlyDictionary<string, int> UpgradedFrom);

/// <summary>Fusion 融合路由统计。FusionRequests 按 request_id 去重后的融合请求次数
/// （一次融合产生 anchor/secondary/analyst 多行，逐行累加会夸大约 4 倍）。</summary>
public sealed record AuditFusionStats(
    int FusionRequests,
    IReadOnlyDictionary<string, int> ByRole);

/// <summary>路由原因聚合（Top N，按请求量降序）。</summary>
public sealed record AuditReasonStats(
    string Reason,
    int Requests,
    int Failures,
    double SuccessRatePct);

/// <summary>按 UTC 日聚合的趋势行。</summary>
public sealed record AuditDayStats(
    string Day,
    int Requests,
    int Successes,
    double CostUsd);

/// <summary>
/// 审计分析服务：对 <see cref="IRequestAuditStore"/> 指定时间窗分页拉取并在内存聚合，
/// 产出与旧离线脚本同口径的报告（总览/分模型/分档/级联/Fusion/路由原因/日趋势）。
/// 对所有 IRequestAuditStore 实现（InMemory/SQLite/MariaDB/Postgres）通用，不改存储契约。
/// </summary>
/// <remarks>
/// 聚合在服务进程内完成（分页批 5000，内存 O(批)），窗口由调用方限定；
/// 路径为管理端低频分析，非请求热路径。延迟分位复用 <see cref="LatencyStatsMath"/>。
/// </remarks>
public sealed class AuditAnalysisService
{
    private const int PageSize = 5000;
    private const int MaxReasonRows = 20;

    /// <summary>模型配置缺失或 Provider 为空时的归组桶名。</summary>
    public const string UnknownProvider = "(未配置)";

    private readonly IRequestAuditStore _store;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly Configuration.AppConfigDbStore? _configDb;

    public AuditAnalysisService(IRequestAuditStore store, IOptionsMonitor<RouterOptions> options,
        Configuration.AppConfigDbStore? configDb = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        _configDb = configDb;
    }

    /// <summary>
    /// 模型路由名 → 供应商合并映射，覆盖优先级：当前配置显式 Provider &gt; 当前配置 BaseUrl
    /// 推断（与 /v1/models 口径一致）&gt; 已删除模型墓碑。删除/改名不再让历史审计归组降级为
    /// "(未配置)"——2026-08 用量分析供应商归属丢失事故的根因是分析时对当前配置实时联表。
    /// </summary>
    private Dictionary<string, string> BuildProviderMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // 墓碑层（最低）：模型已下线后唯一的归组依据。
        if (_configDb is not null)
        {
            foreach (var (name, provider) in LoadProviderTombstones())
                map[name] = provider;
        }

        // 当前配置层：EffectiveProvider 显式优先、推断兜底；"unknown" 端点保留墓碑或归未知桶。
        foreach (var model in _options.CurrentValue.Models)
        {
            var provider = ModelDisplayIds.EffectiveProvider(model);
            if (!string.IsNullOrWhiteSpace(provider)
                && !string.Equals(provider, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                map[model.Name] = provider;
            }
        }

        return map;
    }

    private IReadOnlyDictionary<string, string> LoadProviderTombstones()
    {
        try
        {
            string? json = _configDb?.LoadDocument(Configuration.AppConfigDbStore.ProviderTombstoneScope);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            // 墓碑文档损坏降级空表：该批历史归组回退"(未配置)"，不阻断分析。
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>聚合 [fromUtc, toUtc] 闭区间窗口的审计记录。</summary>
    public AuditAnalysisReport Analyze(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc) fromUtc = fromUtc.ToUniversalTime();
        if (toUtc.Kind != DateTimeKind.Utc) toUtc = toUtc.ToUniversalTime();
        if (fromUtc > toUtc)
            throw new ArgumentException("fromUtc 必须早于 toUtc。");

        int total = 0, successes = 0;
        double totalCost = 0d;
        long promptTokens = 0, completionTokens = 0, cachedTokens = 0;
        var latencies = new List<double>();
        var byModel = new Dictionary<string, ModelAcc>(StringComparer.Ordinal);
        var byProvider = new Dictionary<string, ProviderAcc>(StringComparer.Ordinal);
        var byTier = new Dictionary<string, TierAcc>(StringComparer.Ordinal);
        var byReason = new Dictionary<string, ReasonAcc>(StringComparer.Ordinal);
        var byDay = new Dictionary<string, DayAcc>(StringComparer.Ordinal);
        var upgradedFrom = new Dictionary<string, int>(StringComparer.Ordinal);
        var fusionRoles = new Dictionary<string, int>(StringComparer.Ordinal);
        // Fusion 计数按 request_id 去重：一次融合请求产生多行（secondary/analyst 带 FusionRole），
        // 逐行累加会把 "Fusion N 次" 夸大约 4 倍。无 request_id 的旧记录按行回退计数。
        var fusionRequestIds = new HashSet<string>(StringComparer.Ordinal);
        int fusionRowsWithoutId = 0;
        int cascadeTriggered = 0;

        // 模型路由名 → 供应商：当前配置（显式/推断）+ 已删除模型墓碑的合并映射。
        var providerByModel = BuildProviderMap();

        int offset = 0;
        while (true)
        {
            var (items, totalCount) = _store.GetByTimeRange(fromUtc, toUtc, PageSize, offset);
            if (items.Count == 0) break;

            foreach (var r in items)
            {
                total++;
                bool ok = r.Success;
                if (ok) successes++;
                totalCost += (double)r.Cost;
                promptTokens += r.PromptTokens;
                completionTokens += r.CompletionTokens;
                cachedTokens += r.CachedInputTokens;

                if (ok && r.LatencyMs > 0)
                    latencies.Add(r.LatencyMs);

                var m = byModel.TryGetValue(r.Model, out var ma) ? ma : byModel[r.Model] = new ModelAcc();
                m.Requests++;
                if (!ok) m.Failures++;
                m.Cost += (double)r.Cost;
                m.PromptTokens += r.PromptTokens;
                m.CompletionTokens += r.CompletionTokens;
                m.CachedInputTokens += r.CachedInputTokens;
                if (ok && r.LatencyMs > 0) m.Latencies.Add(r.LatencyMs);

                string provider = providerByModel.TryGetValue(r.Model, out var pv) && !string.IsNullOrEmpty(pv)
                    ? pv
                    : UnknownProvider;
                var pr = byProvider.TryGetValue(provider, out var pa) ? pa : byProvider[provider] = new ProviderAcc();
                pr.Requests++;
                if (!ok) pr.Failures++;
                pr.Cost += (double)r.Cost;
                pr.PromptTokens += r.PromptTokens;
                pr.CachedInputTokens += r.CachedInputTokens;
                pr.CompletionTokens += r.CompletionTokens;
                pr.Models.Add(r.Model);

                string tier = r.RoutedTier.ToString();
                var t = byTier.TryGetValue(tier, out var ta) ? ta : byTier[tier] = new TierAcc();
                t.Requests++;
                if (!ok) t.Failures++;
                t.Cost += (double)r.Cost;

                // 路由归因优先用结构化分类信号（如 code-complex→Strong）聚合——Reason 字符串前缀
                // 含每次都不同的估算值/候选数，同路径请求会发散成大量小组，统计失真。
                // 旧记录无信号时回退 Reason 前 80 字符（口径见 ReasonAcc 注释）。
                string reasonKey = !string.IsNullOrEmpty(r.ClassificationSignal)
                    ? $"{r.ClassificationSignal}→{r.RoutedTier}"
                    : (r.RoutingReason.Length <= 80 ? r.RoutingReason : r.RoutingReason[..80]);
                var rs = byReason.TryGetValue(reasonKey, out var ra) ? ra : byReason[reasonKey] = new ReasonAcc();
                rs.Requests++;
                if (!ok) rs.Failures++;

                string day = r.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var d = byDay.TryGetValue(day, out var da) ? da : byDay[day] = new DayAcc();
                d.Requests++;
                if (ok) d.Successes++;
                d.Cost += (double)r.Cost;

                if (r.CascadeTriggered)
                {
                    cascadeTriggered++;
                    if (!string.IsNullOrEmpty(r.UpgradedFrom))
                        upgradedFrom[r.UpgradedFrom] = upgradedFrom.TryGetValue(r.UpgradedFrom, out int n) ? n + 1 : 1;
                }

                if (!string.IsNullOrEmpty(r.FusionRole))
                {
                    fusionRoles[r.FusionRole] = fusionRoles.TryGetValue(r.FusionRole, out var n) ? n + 1 : 1;
                    if (!string.IsNullOrEmpty(r.RequestId))
                        fusionRequestIds.Add(r.RequestId);
                    else
                        fusionRowsWithoutId++;
                }
            }

            offset += items.Count;
            if (offset >= totalCount || items.Count < PageSize) break;
        }

        latencies.Sort();
        double avg = latencies.Count == 0 ? 0 : latencies.Sum() / latencies.Count;
        double successRatePct = total == 0 ? 0 : 100.0 * successes / total;
        var summary = new AuditAnalysisSummary(
            Successes: successes,
            Failures: total - successes,
            SuccessRatePct: Math.Round(successRatePct, 2),
            TotalCostUsd: Math.Round(totalCost, 6),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            CachedInputTokens: cachedTokens,
            AvgLatencyMs: Math.Round(avg, 1),
            P50LatencyMs: Pct(latencies, 50),
            P95LatencyMs: Pct(latencies, 95),
            P99LatencyMs: Pct(latencies, 99),
            LatencySamples: latencies.Count);

        var modelRows = byModel
            .Select(kv => new AuditModelStats(
                kv.Key, kv.Value.Requests, kv.Value.Failures,
                Math.Round(100.0 * (kv.Value.Requests - kv.Value.Failures) / Math.Max(1, kv.Value.Requests), 2),
                Math.Round(kv.Value.Cost, 6),
                Avg(kv.Value.Latencies),
                P95(kv.Value.Latencies),
                kv.Value.PromptTokens,
                kv.Value.CompletionTokens,
                kv.Value.CachedInputTokens))
            .OrderByDescending(m => m.Requests)
            .ToList();

        var providerRows = byProvider
            .Select(kv => new AuditProviderStats(
                kv.Key, kv.Value.Requests, kv.Value.Failures,
                Math.Round(100.0 * (kv.Value.Requests - kv.Value.Failures) / Math.Max(1, kv.Value.Requests), 2),
                kv.Value.PromptTokens,
                kv.Value.CachedInputTokens,
                kv.Value.CompletionTokens,
                Math.Round(kv.Value.Cost, 6),
                kv.Value.Models.Count))
            .OrderByDescending(p => p.PromptTokens)
            .ToList();

        var tierRows = byTier
            .Select(kv => new AuditTierStats(
                kv.Key, kv.Value.Requests, kv.Value.Failures,
                Math.Round(100.0 * (kv.Value.Requests - kv.Value.Failures) / Math.Max(1, kv.Value.Requests), 2),
                Math.Round(kv.Value.Cost, 6),
                Math.Round(100.0 * kv.Value.Cost / Math.Max(0.000001, totalCost), 2)))
            .OrderByDescending(t => t.Requests)
            .ToList();

        var cascade = new AuditCascadeStats(
            cascadeTriggered,
            Math.Round(100.0 * cascadeTriggered / Math.Max(1, total), 2),
            upgradedFrom.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value));

        var fusion = new AuditFusionStats(
            fusionRequestIds.Count + fusionRowsWithoutId,
            fusionRoles.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value));

        var reasonRows = byReason
            .Select(kv => new AuditReasonStats(
                kv.Key, kv.Value.Requests, kv.Value.Failures,
                Math.Round(100.0 * (kv.Value.Requests - kv.Value.Failures) / Math.Max(1, kv.Value.Requests), 2)))
            .OrderByDescending(r => r.Requests)
            .Take(MaxReasonRows)
            .ToList();

        var dayRows = byDay
            .Select(kv => new AuditDayStats(kv.Key, kv.Value.Requests, kv.Value.Successes, Math.Round(kv.Value.Cost, 6)))
            .OrderBy(d => d.Day)
            .ToList();

        return new AuditAnalysisReport(fromUtc, toUtc, total, summary, modelRows, providerRows, tierRows, cascade, fusion, reasonRows, dayRows)
        {
            ProviderByModel = providerByModel
        };
    }

    private static double Avg(List<double> values)
        => values.Count == 0 ? 0 : Math.Round(values.Sum() / values.Count, 1);

    /// <summary>LatencyStatsMath.Percentile 要求非空列表，这里空样本返回 0。</summary>
    private static double Pct(List<double> sorted, double pct)
        => sorted.Count == 0 ? 0 : Math.Round(LatencyStatsMath.Percentile(sorted, pct), 1);

    private static double P95(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<double>(values);
        sorted.Sort();
        return Pct(sorted, 95);
    }

    private sealed class ModelAcc
    {
        public int Requests, Failures;
        public double Cost;
        public long PromptTokens, CompletionTokens, CachedInputTokens;
        public List<double> Latencies { get; } = new();
    }

    private sealed class ProviderAcc
    {
        public int Requests, Failures;
        public double Cost;
        public long PromptTokens, CachedInputTokens, CompletionTokens;
        public HashSet<string> Models { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TierAcc
    {
        public int Requests, Failures;
        public double Cost;
    }

    private sealed class ReasonAcc
    {
        public int Requests, Failures;
    }

    private sealed class DayAcc
    {
        public int Requests, Successes;
        public double Cost;
    }
}
