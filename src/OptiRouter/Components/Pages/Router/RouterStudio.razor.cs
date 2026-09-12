using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OptiRouter.Components;
using OptiRouter.Components.Pages.Dashboard;
using OptiRouter.Components.Pages.Models;
using OptiRouter.Components.Pages.Requests;
using OptiRouter.Components.Pages.Router;
using OptiRouter.Components.Services;
using OptiRouter.Components.Shared;
using OptiRouter.Configuration;
using OptiRouter.Metrics;
using OptiRouter.Routing;
namespace OptiRouter.Components.Pages.Router;

// UI 大页组件化（#4）：页面逻辑自 .razor 的 @code 块移入 code-behind 分部类，
// 标记与逻辑分离；@inject 生成的属性经分部类同类型解析，行为不变。
public partial class RouterStudio
{
    private ApiService.DashboardMetrics? Metrics;
    private List<ApiService.LearningStateDto> LearningStates = new();
    private ApiService.CalibrationDiagnosticsDto? Calibration;
    private List<ApiService.ConfigChangeDto> ConfigChanges = new();
    private string? LearningStatusMsg;
    private bool LearningStatusOk;

    /// <summary>Fusion 编排模型下拉选项（当前已配置的模型名，含停用项——编排目标可能临时停用但保留配置）。</summary>
    private List<string> FusionModelOptions => Metrics?.Models?.Select(m => m.Name).ToList() ?? new List<string>();

    private async Task ResetLearningState()
    {
        LearningStatusMsg = "重置中...";
        try
        {
            bool confirmed = await JS.InvokeAsync<bool>("confirm", "确定重置全部 Thompson / Bandit 学习状态？\n所有模型将回到均匀先验（含持久化），路由学习从零开始，不可恢复。");
            if (!confirmed)
            {
                LearningStatusMsg = null;
                return;
            }
            var (ok, error) = await Api.ResetLearningAsync();
            LearningStatusOk = ok;
            LearningStatusMsg = ok ? "✓ 学习状态已重置为均匀先验。" : "✗ 重置失败：" + (error ?? "未知错误");
            if (ok)
            {
                await LoadLearning();
            }
        }
        catch (Exception ex)
        {
            LearningStatusOk = false;
            LearningStatusMsg = "✗ 重置异常：" + ex.Message;
        }
    }

    private async Task ExportLearningCsv()
    {
        await JS.InvokeVoidAsync("open", Api.BuildLearningExportUrl(), "_blank");
    }

    private async Task LoadConfigChanges()
    {
        ConfigChanges = await Api.GetConfigChangesAsync(50);
    }

    private static string FormatConfigKey(System.Text.Json.JsonElement change)
        => change.TryGetProperty("key", out var k) && k.GetString() is { } key ? key : "?";

    /// <summary>配置变更值展示：JSON 字符串去引号，null/缺失显示 (未设置)，其余原样。</summary>
    private static string FormatConfigValue(System.Text.Json.JsonElement change, string prop)
    {
        if (!change.TryGetProperty(prop, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null)
            return "(未设置)";
        return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    private static string FormatConfigChangeTime(string ts)
    {
        return DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm:ss")
            : ts;
    }

    /// <summary>配置表单（可变属性供 @bind；dirty 检测用 JSON 序列化快照比较）。</summary>
    private sealed class ConfigForm
    {
        // ① 基础路由
        public bool EnableRuleClassifier { get; set; }
        public bool EnableSemanticRouter { get; set; }
        public bool EnableSessionAffinity { get; set; }
        public bool EnableLatencyAware { get; set; }
        public bool EnableLoadBalance { get; set; }
        public bool EnableKalmanLoadBalance { get; set; }
        public bool EnableCapabilityFilter { get; set; }
        public string DefaultTier { get; set; } = "Medium";
        // ② 可靠性与预算
        public bool EnableFailover { get; set; }
        public int FailoverFailureThreshold { get; set; } = 3;
        public int FailoverCooldownSeconds { get; set; } = 60;
        public int FailoverGlobalTimeoutSeconds { get; set; }
        public int StreamFirstTokenTimeoutMs { get; set; }
        public int StreamHedgeDelayMs { get; set; }
        public bool EnableBudgetGuard { get; set; }
        public bool EnableHealthProbe { get; set; } = true;
        public decimal DailyBudgetUsd { get; set; } = 10.0m;
        public string EnforceOnExhausted { get; set; } = "Degrade";
        // ③ 学习与优化
        public bool EnableThompsonSampling { get; set; }
        public bool EnableContextualBandit { get; set; }
        public double ExplorationEpsilon { get; set; }
        public long ExplorationStarvedN { get; set; }
        public bool EnableResponseCache { get; set; }
        public int ResponseCacheTtlSeconds { get; set; } = 3600;
        public int ResponseCacheMaxEntries { get; set; } = 1000;
        public bool EnableSemanticCache { get; set; }
        public double SemanticCacheSimilarityThreshold { get; set; } = 0.94;
        public int SemanticCacheTtlMinutes { get; set; } = 60;
        public bool EnableCascadeUpgrade { get; set; }
        public double CascadeUpgradeSampleRate { get; set; } = 0.1;
        public bool EnableRegenerateFeedback { get; set; }
        public bool EnableQualityJudge { get; set; }
        public double QualityJudgeSampleRate { get; set; } = 0.2;
        public string QualityJudgeModel { get; set; } = "";
        // ④ 合规与安全
        public bool EnablePiiAnonymization { get; set; }
        public bool EnableDataSovereignty { get; set; }
        public bool EnableContentModeration { get; set; }
        public double ModerationSampleRate { get; set; } = 1.0;
        public double ModerationThreshold { get; set; } = 0.8;
        public bool EnableStreamingComplianceFilter { get; set; }
        public bool EnablePersonaDriftProtection { get; set; } = true;
        public bool EnablePromptCompression { get; set; }
        // ⑤ 高级编排
        public bool EnableFusionRouter { get; set; }
        public string FusionRouterMinComplexity { get; set; } = "Standard";
        public bool EnableFusionMode { get; set; }
        public bool EnableByzantineConsensus { get; set; }
        public bool EnableJsonAstAutoRepair { get; set; } = true;
        public int FusionRouterPanelSize { get; set; } = 3;
        public bool EnableDynamicFusionPanelSize { get; set; }
        public int FusionRouterMinPanelSize { get; set; } = 2;
        public bool EnableFusionDiversity { get; set; }
        public string FusionRouterAnalystModel { get; set; } = "";
        public string FusionRouterAnalystPrompt { get; set; } = "";
        public string FusionRouterOuterModel { get; set; } = "";
        public int FusionRouterMaxOutputTokens { get; set; } = 16000;
        public double FusionRouterTemperature { get; set; }
        public double? FusionRouterPanelTemperature { get; set; }
        public int FusionRouterPanelTimeoutSeconds { get; set; }
        public int FusionMaxParallel { get; set; } = 2;
        public int FusionHedgeDelayMs { get; set; }
        // ⑥ 观测
        public bool EnableDistributedTracing { get; set; }
        public bool AuditStoreRequestContent { get; set; } = false;
        public int AuditRetentionHours { get; set; } = 0;
        public string AlertWebhookUrl { get; set; } = "";
        public int AlertWebhookIntervalSeconds { get; set; } = 30;

        public ConfigForm Clone() => (ConfigForm)MemberwiseClone();
    }

    private ConfigForm Cfg = new();
    private ConfigForm? _baseline;
    private string? _configVersion;
    private bool IsConfigSaving = false;
    private string ConfigStatusMsg = "";
    private bool ConfigStatusOk = true;
    private string? CurrentPresetName;
    private string? SelectedPresetName;
    /// <summary>当前配置（表单值）完全匹配的预设名；null = 自定义配置。按值推导而非记录，微调任一预设键后自然降级为自定义。</summary>
    private string? MatchedPresetName;
    private List<ApiService.PresetSummaryDto> PresetSummaries = new();

    /// <summary>与最近一次保存/加载值的差异字段数（逐字段反射比较）。</summary>
    private int DirtyCount => _baseline is null ? 0 : CountDifferences(_baseline, Cfg);

    private static int CountDifferences(ConfigForm a, ConfigForm b)
    {
        int count = 0;
        foreach (var prop in typeof(ConfigForm).GetProperties())
        {
            if (!Equals(prop.GetValue(a), prop.GetValue(b)))
            {
                count++;
            }
        }
        return count;
    }

    private bool IsDirty(string propName)
    {
        if (_baseline is null) return false;
        var prop = typeof(ConfigForm).GetProperty(propName);
        if (prop is null) return false;
        return !Equals(prop.GetValue(_baseline), prop.GetValue(Cfg));
    }

    private T? Dirty<T>(string propName, T value) where T : struct
        => IsDirty(propName) ? value : null;

    /// <summary>可空数值专用（如 FusionRouterPanelTemperature：null = 沿用主温度）。</summary>
    private double? Dirty(string propName, double? value)
        => IsDirty(propName) ? value : null;

    private string? Dirty(string propName, string value) =>
        IsDirty(propName) ? value : null;

    private void OnThompsonToggled(bool value)
    {
        Cfg.EnableThompsonSampling = value;
        if (value) Cfg.EnableContextualBandit = false;
    }

    private void OnBanditToggled(bool value)
    {
        Cfg.EnableContextualBandit = value;
        if (value) Cfg.EnableThompsonSampling = false;
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            var presets = await Api.GetPresetsAsync();
            if (presets == null) return;
            PresetSummaries = presets.Select(kv => new ApiService.PresetSummaryDto(
                kv.Key,
                kv.Key switch
                {
                    "cost-first" => "成本优先",
                    "balanced" => "均衡",
                    _ => "质量优先"
                },
                kv.Key switch
                {
                    "cost-first" => "Thompson 学习 + 延迟感知 + 响应缓存，默认档 Cheap——尽量省钱。",
                    "balanced" => "Thompson 学习 + 级联升级 10% 采样 + 响应缓存，默认档 Medium。",
                    _ => "默认档 Strong + Fusion 多模型融合 + 拜占庭共识 + 级联升级 30%——尽量答好。"
                },
                kv.Value)).ToList();
            RecomputeMatchedPreset();
        }
        catch (Exception ex)
        {
            ConfigStatusOk = false;
            ConfigStatusMsg = "✗ 路由预设加载失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 推导当前表单值完全匹配哪个预设（预设全部键值一致才算匹配）。
    /// 用于回显"上次应用并保存的是哪个模板"——按值推导而非持久化记录预设名，
    /// 用户微调过任何预设键后自然降级为自定义，不会显示失真的预设名。
    /// </summary>
    private void RecomputeMatchedPreset()
    {
        if (PresetSummaries.Count == 0) return;
        foreach (var preset in PresetSummaries)
        {
            if (preset.Values.All(kv => PresetKeyMatches(kv.Key, kv.Value)))
            {
                MatchedPresetName = preset.Name;
                return;
            }
        }
        MatchedPresetName = null;
    }

    private bool PresetKeyMatches(string key, JsonElement value) => (key, value.ValueKind) switch
    {
        ("EnableThompsonSampling", JsonValueKind.True) => Cfg.EnableThompsonSampling,
        ("EnableThompsonSampling", JsonValueKind.False) => !Cfg.EnableThompsonSampling,
        ("EnableLatencyAware", JsonValueKind.True) => Cfg.EnableLatencyAware,
        ("EnableLatencyAware", JsonValueKind.False) => !Cfg.EnableLatencyAware,
        ("ExplorationEpsilon", JsonValueKind.Number) => Math.Abs(Cfg.ExplorationEpsilon - value.GetDouble()) < 1e-9,
        ("EnableResponseCache", JsonValueKind.True) => Cfg.EnableResponseCache,
        ("EnableResponseCache", JsonValueKind.False) => !Cfg.EnableResponseCache,
        ("EnableCascadeUpgrade", JsonValueKind.True) => Cfg.EnableCascadeUpgrade,
        ("EnableCascadeUpgrade", JsonValueKind.False) => !Cfg.EnableCascadeUpgrade,
        ("CascadeUpgradeSampleRate", JsonValueKind.Number) => Math.Abs(Cfg.CascadeUpgradeSampleRate - value.GetDouble()) < 1e-9,
        ("EnableFusionRouter", JsonValueKind.True) => Cfg.EnableFusionRouter,
        ("EnableFusionRouter", JsonValueKind.False) => !Cfg.EnableFusionRouter,
        ("EnableByzantineConsensus", JsonValueKind.True) => Cfg.EnableByzantineConsensus,
        ("EnableByzantineConsensus", JsonValueKind.False) => !Cfg.EnableByzantineConsensus,
        ("DefaultTier", JsonValueKind.String) => string.Equals(Cfg.DefaultTier, value.GetString(), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>把预设值填入表单（不落盘，用户可在保存前继续微调）。</summary>
    private void ApplyPresetToForm(string presetName)
    {
        var preset = PresetSummaries.FirstOrDefault(p => p.Name == presetName);
        if (preset is null) return;

        foreach (var (key, value) in preset.Values)
        {
            switch (key)
            {
                case "EnableThompsonSampling" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableThompsonSampling = value.GetBoolean();
                    if (value.GetBoolean()) Cfg.EnableContextualBandit = false;
                    break;
                case "EnableLatencyAware" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableLatencyAware = value.GetBoolean();
                    break;
                case "ExplorationEpsilon" when value.ValueKind == JsonValueKind.Number:
                    Cfg.ExplorationEpsilon = value.GetDouble();
                    break;
                case "EnableResponseCache" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableResponseCache = value.GetBoolean();
                    break;
                case "EnableCascadeUpgrade" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableCascadeUpgrade = value.GetBoolean();
                    break;
                case "CascadeUpgradeSampleRate" when value.ValueKind == JsonValueKind.Number:
                    Cfg.CascadeUpgradeSampleRate = value.GetDouble();
                    break;
                case "EnableFusionRouter" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableFusionRouter = value.GetBoolean();
                    break;
                case "EnableByzantineConsensus" when value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False:
                    Cfg.EnableByzantineConsensus = value.GetBoolean();
                    break;
                case "DefaultTier" when value.ValueKind == JsonValueKind.String:
                    Cfg.DefaultTier = value.GetString() ?? "Medium";
                    break;
            }
        }
        SelectedPresetName = presetName;
        RecomputeMatchedPreset();
        ConfigStatusOk = true;
        ConfigStatusMsg = $"✓ 预设「{presetName}」已填充到表单（{DirtyCount} 项待保存变更），可继续微调后保存。";
        ToastService.ShowInfo($"预设「{presetName}」已填充到表单（{DirtyCount} 项待保存变更）", "预设已填充");
    }

    private void DiscardChanges()
    {
        if (_baseline is not null)
        {
            Cfg = _baseline.Clone();
        }
        SelectedPresetName = null;
        RecomputeMatchedPreset();
        ConfigStatusMsg = "";
    }

    private string SandboxInputPrompt = "写一个 C# 快速排序算法，并说明空间复杂度";
    private bool IsSandboxRunning = false;
    private ApiService.SandboxResult? SandboxResultData;

    private bool IsEvalRunning = false;
    private ApiService.EvalReportDto? EvalReportData;
    private bool ShowCustomCases = false;
    private string CustomCasesJson = "";
    private string EvalStatusMsg = "";
    private bool EvalStatusOk = true;
    private List<ApiService.EvalReportDto> EvalBatches = new();
    private string? CompareBaselineId;
    private string? CompareCandidateId;
    private bool IsComparing = false;
    private ApiService.PairedEvalDto? CompareResult;
    private CancellationTokenSource? _evalCts;

    // 语义路由规则：本地暂存列表 + 编辑表单。保存全部才 PUT 落盘。
    private List<ApiService.SemanticRouteDto> SemanticRoutes = new();
    private bool SemanticRouterEnabled;
    // 语义路由规则集的配置文档版本（保存时回传做乐观并发；与主配置共享同一份文档版本）。
    private string? _semanticRoutesVersion;
    private double SemanticThreshold;
    private string SrName = "";
    private string SrTier = "Medium";
    private string SrPhrases = "";
    private string? SrEditingName;
    private string SrStatusMsg = "";
    private bool SrStatusOk = true;
    private bool IsSrSaving = false;

    private PeriodicTimer? _timer;
    private CancellationTokenSource _cts = new();

    private async Task LoadSystemConfig()
    {
        // 不在入口清空 _baseline/_configVersion：加载失败时保留旧值，
        // 保存按钮仍可用（服务端 ExpectedVersion 冲突检查兜底并发风险），
        // 避免保存成功后的重载失败把页面锁死到手动刷新。
        // 初次加载失败时 _baseline 保持 null，保存按钮天然禁用（防止把默认值覆盖到运行配置）。
        try
        {
            var cfg = await Api.GetSystemConfigAsync();
            if (cfg is null)
                throw new InvalidOperationException("配置接口返回空响应。");

            var r = cfg.Routing;
            Cfg = new ConfigForm
            {
                    // ① 基础路由
                    EnableRuleClassifier = r.EnableRuleClassifier,
                    EnableSemanticRouter = r.EnableSemanticRouter,
                    EnableSessionAffinity = r.EnableSessionAffinity,
                    EnableLatencyAware = r.EnableLatencyAware,
                    EnableLoadBalance = r.EnableLoadBalance,
                    EnableKalmanLoadBalance = r.EnableKalmanLoadBalance,
                    EnableCapabilityFilter = r.EnableCapabilityFilter,
                    DefaultTier = string.IsNullOrEmpty(r.DefaultTier) ? "Medium" : r.DefaultTier,
                    // ② 可靠性与预算
                    EnableFailover = r.EnableFailover,
                    FailoverFailureThreshold = r.FailoverFailureThreshold,
                    FailoverCooldownSeconds = r.FailoverCooldownSeconds,
                    FailoverGlobalTimeoutSeconds = r.FailoverGlobalTimeoutSeconds,
                    StreamFirstTokenTimeoutMs = r.StreamFirstTokenTimeoutMs,
                    StreamHedgeDelayMs = r.StreamHedgeDelayMs,
                    EnableBudgetGuard = r.EnableBudgetGuard,
                    EnableHealthProbe = r.EnableHealthProbe,
                    DailyBudgetUsd = cfg.Budget.DailyBudgetUsd,
                    EnforceOnExhausted = string.IsNullOrEmpty(cfg.Budget.EnforceOnExhausted) ? "Degrade" : cfg.Budget.EnforceOnExhausted,
                    // ③ 学习与优化
                    EnableThompsonSampling = r.EnableThompsonSampling,
                    EnableContextualBandit = r.EnableContextualBandit,
                    ExplorationEpsilon = r.ExplorationEpsilon,
                    ExplorationStarvedN = r.ExplorationStarvedN,
                    EnableResponseCache = r.EnableResponseCache,
                    ResponseCacheTtlSeconds = r.ResponseCacheTtlSeconds,
                    ResponseCacheMaxEntries = r.ResponseCacheMaxEntries,
                    EnableSemanticCache = r.EnableSemanticCache,
                    SemanticCacheSimilarityThreshold = r.SemanticCacheSimilarityThreshold,
                    SemanticCacheTtlMinutes = r.SemanticCacheTtlMinutes,
                    EnableCascadeUpgrade = r.EnableCascadeUpgrade,
                    CascadeUpgradeSampleRate = r.CascadeUpgradeSampleRate,
                    EnableRegenerateFeedback = r.EnableRegenerateFeedback,
                    EnableQualityJudge = r.EnableQualityJudge,
                    QualityJudgeSampleRate = r.QualityJudgeSampleRate,
                    QualityJudgeModel = r.QualityJudgeModel ?? "",
                    // ④ 合规与安全
                    EnablePiiAnonymization = r.EnablePiiAnonymization,
                    EnableDataSovereignty = r.EnableDataSovereignty,
                    EnableContentModeration = r.EnableContentModeration,
                    ModerationSampleRate = r.ModerationSampleRate,
                    ModerationThreshold = r.ModerationThreshold,
                    EnableStreamingComplianceFilter = r.EnableStreamingComplianceFilter,
                    EnablePersonaDriftProtection = r.EnablePersonaDriftProtection,
                    EnablePromptCompression = r.EnablePromptCompression,
                    // ⑤ 高级编排
                    EnableFusionRouter = r.EnableFusionRouter,
                    FusionRouterMinComplexity = string.IsNullOrEmpty(r.FusionRouterMinComplexity) ? "Standard" : r.FusionRouterMinComplexity,
                    EnableFusionMode = r.EnableFusionMode,
                    EnableByzantineConsensus = r.EnableByzantineConsensus,
                    EnableJsonAstAutoRepair = r.EnableJsonAstAutoRepair,
                    FusionRouterPanelSize = r.FusionRouterPanelSize,
                    EnableDynamicFusionPanelSize = r.EnableDynamicFusionPanelSize,
                    FusionRouterMinPanelSize = r.FusionRouterMinPanelSize,
                    EnableFusionDiversity = r.EnableFusionDiversity,
                    FusionRouterAnalystModel = r.FusionRouterAnalystModel ?? "",
                    FusionRouterAnalystPrompt = r.FusionRouterAnalystPrompt ?? "",
                    FusionRouterOuterModel = r.FusionRouterOuterModel ?? "",
                    FusionRouterMaxOutputTokens = r.FusionRouterMaxOutputTokens,
                    FusionRouterTemperature = r.FusionRouterTemperature,
                    FusionRouterPanelTemperature = r.FusionRouterPanelTemperature,
                    FusionRouterPanelTimeoutSeconds = r.FusionRouterPanelTimeoutSeconds,
                    FusionMaxParallel = r.FusionMaxParallel,
                    FusionHedgeDelayMs = r.FusionHedgeDelayMs,
                    // ⑥ 观测
                    EnableDistributedTracing = r.EnableDistributedTracing,
                    AuditStoreRequestContent = r.AuditStoreRequestContent,
                    AuditRetentionHours = r.AuditRetentionHours,
                    AlertWebhookUrl = r.AlertWebhookUrl ?? "",
                    AlertWebhookIntervalSeconds = r.AlertWebhookIntervalSeconds
            };
            _baseline = Cfg.Clone();
            _configVersion = cfg.Version;
            CurrentPresetName = r.Preset;
        }
        catch (Exception ex)
        {
            // 配置加载失败必须显式警示：表单当前显示的是代码默认值，
            // 管理员若不知情直接保存会把默认值覆盖到运行配置。
            ConfigStatusOk = false;
            ConfigStatusMsg = "✗ 配置加载失败，下方表单为默认值（请勿直接保存）：" + ex.Message;
        }
    }

    private async Task SaveSystemConfig()
    {
        if (_baseline is null || string.IsNullOrWhiteSpace(_configVersion))
        {
            ConfigStatusOk = false;
            ConfigStatusMsg = "✗ 配置尚未成功加载，禁止保存。请刷新或重试加载。";
            return;
        }
        if (DirtyCount == 0)
            return;

        IsConfigSaving = true;
        ConfigStatusMsg = "";
        try
        {
            var req = new ApiService.UpdateSystemConfigRequest
            {
                ExpectedVersion = _configVersion,
                EnableRuleClassifier = Dirty(nameof(ConfigForm.EnableRuleClassifier), Cfg.EnableRuleClassifier),
                EnableSemanticRouter = Dirty(nameof(ConfigForm.EnableSemanticRouter), Cfg.EnableSemanticRouter),
                EnableSessionAffinity = Dirty(nameof(ConfigForm.EnableSessionAffinity), Cfg.EnableSessionAffinity),
                EnableLatencyAware = Dirty(nameof(ConfigForm.EnableLatencyAware), Cfg.EnableLatencyAware),
                EnableLoadBalance = Dirty(nameof(ConfigForm.EnableLoadBalance), Cfg.EnableLoadBalance),
                EnableKalmanLoadBalance = Dirty(nameof(ConfigForm.EnableKalmanLoadBalance), Cfg.EnableKalmanLoadBalance),
                EnableCapabilityFilter = Dirty(nameof(ConfigForm.EnableCapabilityFilter), Cfg.EnableCapabilityFilter),
                DefaultTier = Dirty(nameof(ConfigForm.DefaultTier), Cfg.DefaultTier),
                EnableFailover = Dirty(nameof(ConfigForm.EnableFailover), Cfg.EnableFailover),
                FailoverFailureThreshold = Dirty(nameof(ConfigForm.FailoverFailureThreshold), Cfg.FailoverFailureThreshold),
                FailoverCooldownSeconds = Dirty(nameof(ConfigForm.FailoverCooldownSeconds), Cfg.FailoverCooldownSeconds),
                FailoverGlobalTimeoutSeconds = Dirty(nameof(ConfigForm.FailoverGlobalTimeoutSeconds), Cfg.FailoverGlobalTimeoutSeconds),
                StreamFirstTokenTimeoutMs = Dirty(nameof(ConfigForm.StreamFirstTokenTimeoutMs), Cfg.StreamFirstTokenTimeoutMs),
                StreamHedgeDelayMs = Dirty(nameof(ConfigForm.StreamHedgeDelayMs), Cfg.StreamHedgeDelayMs),
                EnableBudgetGuard = Dirty(nameof(ConfigForm.EnableBudgetGuard), Cfg.EnableBudgetGuard),
                EnableHealthProbe = Dirty(nameof(ConfigForm.EnableHealthProbe), Cfg.EnableHealthProbe),
                DailyBudgetUsd = Dirty(nameof(ConfigForm.DailyBudgetUsd), Cfg.DailyBudgetUsd),
                EnforceOnExhausted = Dirty(nameof(ConfigForm.EnforceOnExhausted), Cfg.EnforceOnExhausted),
                EnableThompsonSampling = Dirty(nameof(ConfigForm.EnableThompsonSampling), Cfg.EnableThompsonSampling),
                EnableContextualBandit = Dirty(nameof(ConfigForm.EnableContextualBandit), Cfg.EnableContextualBandit),
                ExplorationEpsilon = Dirty(nameof(ConfigForm.ExplorationEpsilon), Cfg.ExplorationEpsilon),
                ExplorationStarvedN = Dirty(nameof(ConfigForm.ExplorationStarvedN), Cfg.ExplorationStarvedN),
                EnableResponseCache = Dirty(nameof(ConfigForm.EnableResponseCache), Cfg.EnableResponseCache),
                ResponseCacheTtlSeconds = Dirty(nameof(ConfigForm.ResponseCacheTtlSeconds), Cfg.ResponseCacheTtlSeconds),
                ResponseCacheMaxEntries = Dirty(nameof(ConfigForm.ResponseCacheMaxEntries), Cfg.ResponseCacheMaxEntries),
                EnableSemanticCache = Dirty(nameof(ConfigForm.EnableSemanticCache), Cfg.EnableSemanticCache),
                SemanticCacheSimilarityThreshold = Dirty(nameof(ConfigForm.SemanticCacheSimilarityThreshold), Cfg.SemanticCacheSimilarityThreshold),
                SemanticCacheTtlMinutes = Dirty(nameof(ConfigForm.SemanticCacheTtlMinutes), Cfg.SemanticCacheTtlMinutes),
                EnableCascadeUpgrade = Dirty(nameof(ConfigForm.EnableCascadeUpgrade), Cfg.EnableCascadeUpgrade),
                CascadeUpgradeSampleRate = Dirty(nameof(ConfigForm.CascadeUpgradeSampleRate), Cfg.CascadeUpgradeSampleRate),
                EnableRegenerateFeedback = Dirty(nameof(ConfigForm.EnableRegenerateFeedback), Cfg.EnableRegenerateFeedback),
                EnableQualityJudge = Dirty(nameof(ConfigForm.EnableQualityJudge), Cfg.EnableQualityJudge),
                QualityJudgeSampleRate = Dirty(nameof(ConfigForm.QualityJudgeSampleRate), Cfg.QualityJudgeSampleRate),
                QualityJudgeModel = Dirty(nameof(ConfigForm.QualityJudgeModel), Cfg.QualityJudgeModel),
                EnablePiiAnonymization = Dirty(nameof(ConfigForm.EnablePiiAnonymization), Cfg.EnablePiiAnonymization),
                EnableDataSovereignty = Dirty(nameof(ConfigForm.EnableDataSovereignty), Cfg.EnableDataSovereignty),
                EnableContentModeration = Dirty(nameof(ConfigForm.EnableContentModeration), Cfg.EnableContentModeration),
                ModerationSampleRate = Dirty(nameof(ConfigForm.ModerationSampleRate), Cfg.ModerationSampleRate),
                ModerationThreshold = Dirty(nameof(ConfigForm.ModerationThreshold), Cfg.ModerationThreshold),
                EnableStreamingComplianceFilter = Dirty(nameof(ConfigForm.EnableStreamingComplianceFilter), Cfg.EnableStreamingComplianceFilter),
                EnablePersonaDriftProtection = Dirty(nameof(ConfigForm.EnablePersonaDriftProtection), Cfg.EnablePersonaDriftProtection),
                EnablePromptCompression = Dirty(nameof(ConfigForm.EnablePromptCompression), Cfg.EnablePromptCompression),
                EnableFusionRouter = Dirty(nameof(ConfigForm.EnableFusionRouter), Cfg.EnableFusionRouter),
                FusionRouterMinComplexity = Dirty(nameof(ConfigForm.FusionRouterMinComplexity), Cfg.FusionRouterMinComplexity),
                EnableFusionMode = Dirty(nameof(ConfigForm.EnableFusionMode), Cfg.EnableFusionMode),
                EnableByzantineConsensus = Dirty(nameof(ConfigForm.EnableByzantineConsensus), Cfg.EnableByzantineConsensus),
                EnableJsonAstAutoRepair = Dirty(nameof(ConfigForm.EnableJsonAstAutoRepair), Cfg.EnableJsonAstAutoRepair),
                FusionRouterPanelSize = Dirty(nameof(ConfigForm.FusionRouterPanelSize), Cfg.FusionRouterPanelSize),
                EnableDynamicFusionPanelSize = Dirty(nameof(ConfigForm.EnableDynamicFusionPanelSize), Cfg.EnableDynamicFusionPanelSize),
                FusionRouterMinPanelSize = Dirty(nameof(ConfigForm.FusionRouterMinPanelSize), Cfg.FusionRouterMinPanelSize),
                EnableFusionDiversity = Dirty(nameof(ConfigForm.EnableFusionDiversity), Cfg.EnableFusionDiversity),
                FusionRouterAnalystModel = Dirty(nameof(ConfigForm.FusionRouterAnalystModel), Cfg.FusionRouterAnalystModel),
                FusionRouterAnalystPrompt = Dirty(nameof(ConfigForm.FusionRouterAnalystPrompt), Cfg.FusionRouterAnalystPrompt),
                FusionRouterOuterModel = Dirty(nameof(ConfigForm.FusionRouterOuterModel), Cfg.FusionRouterOuterModel),
                FusionRouterMaxOutputTokens = Dirty(nameof(ConfigForm.FusionRouterMaxOutputTokens), Cfg.FusionRouterMaxOutputTokens),
                FusionRouterTemperature = Dirty(nameof(ConfigForm.FusionRouterTemperature), Cfg.FusionRouterTemperature),
                FusionRouterPanelTemperature = Dirty(nameof(ConfigForm.FusionRouterPanelTemperature), Cfg.FusionRouterPanelTemperature),
                FusionRouterPanelTimeoutSeconds = Dirty(nameof(ConfigForm.FusionRouterPanelTimeoutSeconds), Cfg.FusionRouterPanelTimeoutSeconds),
                FusionMaxParallel = Dirty(nameof(ConfigForm.FusionMaxParallel), Cfg.FusionMaxParallel),
                FusionHedgeDelayMs = Dirty(nameof(ConfigForm.FusionHedgeDelayMs), Cfg.FusionHedgeDelayMs),
                EnableDistributedTracing = Dirty(nameof(ConfigForm.EnableDistributedTracing), Cfg.EnableDistributedTracing),
                AuditStoreRequestContent = Dirty(nameof(ConfigForm.AuditStoreRequestContent), Cfg.AuditStoreRequestContent),
                AuditRetentionHours = Dirty(nameof(ConfigForm.AuditRetentionHours), Cfg.AuditRetentionHours),
                AlertWebhookUrl = Dirty(nameof(ConfigForm.AlertWebhookUrl), Cfg.AlertWebhookUrl),
                AlertWebhookIntervalSeconds = Dirty(nameof(ConfigForm.AlertWebhookIntervalSeconds), Cfg.AlertWebhookIntervalSeconds)
            };

            var (ok, error, newVersion) = await Api.UpdateSystemConfigAsync(req);
            if (ok)
            {
                // 先采纳保存响应的新版本：即使随后的重载失败，下一次保存也不会
                // 因持有过期版本被 409 拒绝。
                if (!string.IsNullOrWhiteSpace(newVersion))
                {
                    _configVersion = newVersion;
                    _semanticRoutesVersion = newVersion;
                }
                await LoadSystemConfig();
                if (_baseline is null)
                    return;
                ConfigStatusOk = true;
                ConfigStatusMsg = "✓ 配置已热更新并在数据平面实时生效！";
                ToastService.ShowSuccess("配置已热更新并在数据平面实时生效！", "配置已保存");
                await LoadMetrics();
                await LoadConfigChanges();
            }
            else
            {
                ConfigStatusOk = false;
                ConfigStatusMsg = "✗ 保存被拒绝: " + ExtractErrorBody(error);
                ToastService.ShowError("保存被拒绝: " + ExtractErrorBody(error), "保存失败");
            }
        }
        catch (Exception ex)
        {
            ConfigStatusOk = false;
            ConfigStatusMsg = "✗ 错误: " + ex.Message;
            ToastService.ShowError("保存异常: " + ex.Message, "系统错误");
        }
        finally
        {
            IsConfigSaving = false;
        }
    }

    /// <summary>从失败响应体提取可读错误（后端 400 返回 {"error":"..."} 校验消息）。</summary>
    private static string ExtractErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "请求失败（无响应体）";
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body)?["error"];
            if (node is not null) return node.ToString();
        }
        catch { }
        return body;
    }

    private async Task RunSandbox()
    {
        if (string.IsNullOrWhiteSpace(SandboxInputPrompt)) return;
        IsSandboxRunning = true;
        SandboxError = null;
        try
        {
            var (result, error) = await Api.RunSandboxRouteAsync(SandboxInputPrompt, _cts.Token);
            if (result != null)
            {
                SandboxResultData = result;
            }
            else
            {
                SandboxResultData = null;
                SandboxError = error ?? "未知错误";
            }
        }
        catch (Exception ex)
        {
            SandboxError = ex.Message;
        }
        finally
        {
            IsSandboxRunning = false;
        }
    }

    private string? SandboxError;

    private async Task RunEval()
    {
        if (IsEvalRunning) return;

        List<ApiService.EvalCaseDto>? cases = null;
        if (ShowCustomCases && !string.IsNullOrWhiteSpace(CustomCasesJson))
        {
            try
            {
                cases = System.Text.Json.JsonSerializer.Deserialize<List<ApiService.EvalCaseDto>>(
                    CustomCasesJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                EvalStatusOk = false;
                EvalStatusMsg = "✗ 题库 JSON 解析失败: " + ex.Message;
                return;
            }
            if (cases is null || cases.Count == 0)
            {
                EvalStatusOk = false;
                EvalStatusMsg = "✗ 题库 JSON 不是有效数组或为空。";
                return;
            }
        }

        IsEvalRunning = true;
        EvalStatusMsg = "";
        using var evalCts = new CancellationTokenSource();
        _evalCts = evalCts;
        try
        {
            var (report, error) = await Api.RunEvalBenchmarkAsync(cases, evalCts.Token);
            if (report != null)
            {
                evalCts.Token.ThrowIfCancellationRequested();
                EvalReportData = report;
                EvalStatusOk = true;
                if (!evalCts.IsCancellationRequested)
                {
                    await LoadEvalBatches();
                }
            }
            else
            {
                EvalStatusOk = false;
                EvalStatusMsg = "✗ 评测失败: " + ExtractErrorBody(error);
            }
        }
        catch (OperationCanceledException) when (evalCts.IsCancellationRequested)
        {
            // 离页取消不属于评测失败，不在页面显示错误。
        }
        catch (Exception ex)
        {
            EvalStatusOk = false;
            EvalStatusMsg = "✗ 错误: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_evalCts, evalCts))
            {
                _evalCts = null;
            }
            IsEvalRunning = false;
        }
    }

    private void ToggleCustomCases() => ShowCustomCases = !ShowCustomCases;

    private void FillSampleCases()
    {
        CustomCasesJson = System.Text.Json.JsonSerializer.Serialize(new List<ApiService.EvalCaseDto>
        {
            new("q-01", "解释 TCP 三次握手的过程", "SYN → SYN+ACK → ACK，建立可靠连接", "tech", 5000),
            new("q-02", "写一个 JavaScript 防抖函数", "debounce 返回包装函数，延迟触发最后一次调用", "coding", 5000),
            new("q-03", "1+1 等于几?", "2", "math", 3000)
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private async Task LoadEvalBatches()
    {
        EvalBatches = await Api.GetEvalBatchesAsync();
    }

    private async Task RunCompare()
    {
        if (string.IsNullOrEmpty(CompareBaselineId) || string.IsNullOrEmpty(CompareCandidateId)) return;
        IsComparing = true;
        EvalStatusMsg = "";
        try
        {
            var (report, error) = await Api.CompareEvalBatchesAsync(CompareBaselineId, CompareCandidateId);
            if (report != null)
            {
                CompareResult = report;
            }
            else
            {
                EvalStatusOk = false;
                EvalStatusMsg = "✗ 对比失败: " + ExtractErrorBody(error);
            }
        }
        finally
        {
            IsComparing = false;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadSystemConfig();
        await LoadPresetsAsync();
        await LoadMetrics();
        await LoadLearning();
        await LoadEvalBatches();
        await LoadSemanticRoutes();
        await LoadConfigChanges();

        _timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        _ = PollMetricsLoopAsync();
    }

    private async Task PollMetricsLoopAsync()
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(_cts.Token))
            {
                await LoadMetrics();
                await LoadLearning();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { } // circuit 拆除后 dispatcher 失效，轮询自行退出
    }

    private async Task LoadMetrics()
    {
        try
        {
            Metrics = await Api.GetMetricsAsync();
            MetricsError = null;
        }
        catch (Exception ex)
        {
            // 轮询失败保留上一次数据，仅更新错误提示
            MetricsError = "引擎状态刷新失败：" + ex.Message;
        }
    }

    private string? MetricsError;
    private string? LearningError;

    private async Task LoadLearning()
    {
        try
        {
            LearningStates = await Api.GetLearningAsync();
            Calibration = await Api.GetCalibrationDiagnosticsAsync();
            LearningError = null;
        }
        catch (Exception ex)
        {
            LearningError = "学习状态刷新失败：" + ex.Message;
        }
    }

    private async Task LoadSemanticRoutes()
    {
        try
        {
            var dto = await Api.GetSemanticRoutesAsync();
            if (dto != null)
            {
                SemanticRoutes = dto.Routes?.ToList() ?? new List<ApiService.SemanticRouteDto>();
                SemanticRouterEnabled = dto.Enabled;
                SemanticThreshold = dto.SimilarityThreshold;
                _semanticRoutesVersion = dto.Version;
            }
        }
        catch (Exception ex)
        {
            ConfigStatusOk = false;
            ConfigStatusMsg = "✗ 语义路由规则加载失败：" + ex.Message;
        }
    }

    private void ApplySemanticRouteForm()
    {
        string name = SrName.Trim();
        var phrases = SrPhrases
            .Split('\n')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (name.Length == 0 || phrases.Count == 0)
        {
            SrStatusOk = false;
            SrStatusMsg = "✗ 规则名称与至少一条示例短语为必填。";
            return;
        }
        // 查重排除自身（同名编辑合法）；改名等同删旧建新。先非破坏性校验，再更新列表。
        bool isSelf = SrEditingName is not null && string.Equals(SrEditingName, name, StringComparison.OrdinalIgnoreCase);
        if (!isSelf && SemanticRoutes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SrStatusOk = false;
            SrStatusMsg = $"✗ 规则名称已存在: {name}";
            return;
        }
        if (SrEditingName is not null)
        {
            SemanticRoutes.RemoveAll(r => string.Equals(r.Name, SrEditingName, StringComparison.OrdinalIgnoreCase));
        }

        SemanticRoutes.Add(new ApiService.SemanticRouteDto(name, phrases, SrTier));
        SrName = "";
        SrPhrases = "";
        SrEditingName = null;
        SrStatusMsg = "";
    }

    private void EditSemanticRoute(ApiService.SemanticRouteDto route)
    {
        SrEditingName = route.Name;
        SrName = route.Name;
        SrTier = route.TargetTier;
        SrPhrases = string.Join("\n", route.Phrases);
        SrStatusMsg = "";
    }

    private void CancelSemanticRouteEdit()
    {
        SrEditingName = null;
        SrName = "";
        SrPhrases = "";
    }

    private void RemoveSemanticRoute(ApiService.SemanticRouteDto route)
    {
        SemanticRoutes.RemoveAll(r => string.Equals(r.Name, route.Name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(SrEditingName, route.Name, StringComparison.OrdinalIgnoreCase))
        {
            CancelSemanticRouteEdit();
        }
    }

    private async Task SaveSemanticRoutes()
    {
        IsSrSaving = true;
        SrStatusMsg = "";
        try
        {
            var payload = SemanticRoutes
                .Select(r => new ApiService.SemanticRouteUpsertDto(r.Name, r.Phrases, r.TargetTier))
                .ToList();
            var (ok, error, conflict, version) = await Api.UpdateSemanticRoutesAsync(payload, _semanticRoutesVersion);
            SrStatusOk = ok;
            SrStatusMsg = ok
                ? $"✓ {SemanticRoutes.Count} 条语义路由已保存并热生效。"
                : conflict
                    ? "✗ 规则已被并发修改，已重新加载最新规则，请核对后重试保存。"
                    : "✗ 保存被拒绝: " + ExtractErrorBody(error);
            if (ok)
            {
                ToastService.ShowSuccess($"{SemanticRoutes.Count} 条语义路由已保存并热生效", "语义规则保存");
                // 语义路由与主配置共享同一份配置文档版本：保存成功后同步版本，
                // 避免随后的主配置保存因持有过期版本被 409 拒绝。
                if (!string.IsNullOrWhiteSpace(version))
                {
                    _configVersion = version;
                    _semanticRoutesVersion = version;
                }
            }
            else if (conflict)
            {
                ToastService.ShowWarning("规则已被并发修改，已重新加载最新规则，请核对后重试保存", "并发冲突");
            }
            else
            {
                ToastService.ShowError("保存被拒绝: " + ExtractErrorBody(error), "规则保存失败");
            }
            // 成功或版本冲突都需要重载规则集（刷新版本号与规则内容）。
            if (ok || conflict)
            {
                await LoadSemanticRoutes();
            }
        }
        catch (Exception ex)
        {
            SrStatusOk = false;
            SrStatusMsg = "✗ 错误: " + ex.Message;
            ToastService.ShowError("保存异常: " + ex.Message, "系统错误");
        }
        finally
        {
            IsSrSaving = false;
        }
    }

    private static string Truncate(string val, int maxLen)
    {
        if (string.IsNullOrEmpty(val)) return "-";
        return val.Length <= maxLen ? val : val[..maxLen] + "...";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _evalCts?.Cancel();
        _cts.Dispose();
        _timer?.Dispose();
    }
}
