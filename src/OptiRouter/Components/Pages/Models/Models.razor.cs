namespace OptiRouter.Components.Pages.Models;
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


// UI 大页组件化（#4）：页面逻辑自 .razor 的 @code 块移入 code-behind 分部类，
// 标记与逻辑分离；@inject 生成的属性经分部类同类型解析，行为不变。
public partial class Models
{
    private List<ApiService.ModelDto> ModelList = new();
    private string ModelSortOrder = "default";

    private IEnumerable<ApiService.ModelDto> SortedModelList => ModelSortOrder switch
    {
        "tier" => ModelList
            .OrderBy(m => TierSortRank(m.Tier))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        "name" => ModelList
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        "provider" => ModelList
            .OrderBy(m => string.IsNullOrWhiteSpace(m.Provider))
            .ThenBy(m => m.Provider ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        _ => ModelList
    };

    private static int TierSortRank(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "strong" => 0,
        "medium" => 1,
        "cheap" => 2,
        _ => 3
    };

    private string? ErrorMsg;
    private HashSet<string> ProbingModels = new();
    private Dictionary<string, ProbeView> ProbeResults = new();

    /// <summary>连通状态列视图：探活结果 + 发生时间（手动探活与后台探活留痕共用）。</summary>
    private sealed record ProbeView(bool Success, long LatencyMs, string? Message, string? Error, DateTime TimestampUtc);

    // 列表 ApiKey 显示状态：模型名 → 完整密钥（仅管理员按需 reveal，关闭即移除）。
    private Dictionary<string, string> RevealedKeys = new();
    private HashSet<string> TogglingModels = new();

    // 弹窗（添加/编辑共用一套表单）。
    private bool ShowModal;
    private bool IsEditing;
    private string? EditingName;
    // 复制模式：非编辑（走创建路径），但表单预填源模型全部字段，标题提示来源。
    private string? CopySourceName;
    private bool IsSaving;
    private ModelForm Form = new();
    // 弹窗内 ApiKey 默认明文（用户要求）；可切回密文。
    private bool ShowModalApiKey = true;

    private bool IsLoadingModels = true;
    private bool HealthLoading = true;
    private List<ApiService.ModelInfo> ModelHealth = new();

    // ── 从上游拉取 ──
    private bool ShowDiscoverModal;
    private int DiscoverStep = 1;
    private string DiscoverSourceTab = "existing";
    private string DiscoverSelectedModelName = "";
    private string DiscoverManualBaseUrl = "";
    private string DiscoverManualApiKey = "";
    private string DiscoverManualProtocol = "OpenAI";
    private string? DiscoverError;
    private bool IsDiscovering;
    private List<ApiService.DiscoveredModel> DiscoveredModels = new();
    private List<DiscoverRow> DiscoverSelected = new();
    private bool IsBulkAdding;
    private string BulkAddTier = "Medium";
    private int BulkAddMaxContextTokens = 200_000;
    private string BulkAddEnabled = "true";
    private string BulkAddDeployType = "public";
    private string DiscoverSearchText = "";
    private string DiscoverFilterTab = "all";
    /// <summary>当前拉取会话的源 BaseUrl/Protocol/ApiKey（Step 1 锁定），Step 2 批量添加时回填到新模型。</summary>
    private string DiscoverSourceBaseUrl = "";
    private string DiscoverSourceProtocol = "OpenAI";
    private string? DiscoverSourceApiKey;
    /// <summary>已有模型源拉取时的凭证源模型名（批量导入经服务端 ApiKeySourceModel 复制 key，明文不出服务端）；手动输入模式为 null。</summary>
    private string? DiscoverSourceModelName;

    private record ProviderPreset(string Name, string BaseUrl, string Protocol, string Icon, string Hint);
    private static readonly ProviderPreset[] Presets = new[]
    {
        new ProviderPreset("DeepSeek", "https://api.deepseek.com/v1", "OpenAI", "🤖", "DeepSeek 官方开放平台"),
        new ProviderPreset("硅基流动", "https://api.siliconflow.cn/v1", "OpenAI", "⚡", "SiliconFlow 聚合大模型"),
        new ProviderPreset("OpenAI", "https://api.openai.com/v1", "OpenAI", "🌐", "OpenAI 官方端点"),
        new ProviderPreset("Google Gemini", "https://generativelanguage.googleapis.com", "Gemini", "💎", "Google Gemini 官方 API"),
        new ProviderPreset("Ollama 本地", "http://localhost:11434/v1", "OpenAI", "🦙", "本地开源大模型 (免 Key)"),
        new ProviderPreset("OpenRouter", "https://openrouter.ai/api/v1", "OpenAI", "🔀", "全球多模型聚合网关"),
        new ProviderPreset("月之暗面 Kimi", "https://api.moonshot.cn/v1", "OpenAI", "🌙", "Moonshot AI"),
        new ProviderPreset("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "OpenAI", "🌟", "智谱大模型开放平台"),
        new ProviderPreset("零一万物", "https://api.lingyiwanwu.com/v1", "OpenAI", "🔮", "01.AI 开放平台"),
        new ProviderPreset("vLLM / 本地", "http://localhost:8000/v1", "OpenAI", "🚀", "自托管 vLLM / SGLang (免 Key)"),
    };

    private void ApplyPreset(ProviderPreset p)
    {
        DiscoverManualBaseUrl = p.BaseUrl;
        DiscoverManualProtocol = p.Protocol;
        if (p.Hint.Contains("免 Key", StringComparison.OrdinalIgnoreCase))
        {
            DiscoverManualApiKey = string.Empty;
        }
    }

    // ── 添加/编辑弹窗内拉取上游模型 ──
    private bool IsFormDiscovering;
    private List<ApiService.DiscoveredModel> FormDiscoveredModels = new();
    private string? FormDiscoverError;
    private string FormModelSearch = "";

    private IEnumerable<ApiService.DiscoveredModel> FilteredFormDiscoveredModels =>
        string.IsNullOrWhiteSpace(FormModelSearch)
            ? FormDiscoveredModels
            : FormDiscoveredModels.Where(m =>
                (m.Id?.Contains(FormModelSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Name?.Contains(FormModelSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.OwnedBy?.Contains(FormModelSearch, StringComparison.OrdinalIgnoreCase) ?? false));

    private IEnumerable<DiscoverRow> FilteredDiscoverSelected
    {
        get
        {
            var query = DiscoverSelected.AsEnumerable();
            if (DiscoverFilterTab == "available")
                query = query.Where(r => !r.AlreadyExists && !r.Added);
            else if (DiscoverFilterTab == "existing")
                query = query.Where(r => r.AlreadyExists || r.Added);

            if (!string.IsNullOrWhiteSpace(DiscoverSearchText))
            {
                query = query.Where(r =>
                    (r.Model.Id?.Contains(DiscoverSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Model.Name?.Contains(DiscoverSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (r.Model.OwnedBy?.Contains(DiscoverSearchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            return query;
        }
    }

    private bool AllAvailableSelected =>
        DiscoverSelected.Any(r => !r.AlreadyExists && !r.Added) &&
        DiscoverSelected.Where(r => !r.AlreadyExists && !r.Added).All(r => r.Selected);

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        bool isChecked = (bool)(e.Value ?? false);
        foreach (var r in DiscoverSelected.Where(r => !r.AlreadyExists && !r.Added))
        {
            r.Selected = isChecked;
        }
    }

    private void SelectAllAvailable()
    {
        foreach (var r in DiscoverSelected.Where(r => !r.AlreadyExists && !r.Added))
        {
            r.Selected = true;
        }
    }

    private void DeselectAll()
    {
        foreach (var r in DiscoverSelected)
        {
            r.Selected = false;
        }
    }

    private void InvertSelection()
    {
        foreach (var r in DiscoverSelected.Where(r => !r.AlreadyExists && !r.Added))
        {
            r.Selected = !r.Selected;
        }
    }

    /// <summary>拉取模态的中间行（含勾选与添加结果）。</summary>
    private sealed class DiscoverRow
    {
        public ApiService.DiscoveredModel Model { get; init; } = default!;
        public bool Selected { get; set; }
        public bool AlreadyExists { get; set; }
        public bool Added { get; set; }
        public string? AddError { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadModels();
        await LoadModelHealth();
    }

    private async Task RefreshModelHealth()
    {
        HealthLoading = true;
        StateHasChanged();
        await LoadModelHealth();
        if (string.IsNullOrEmpty(HealthError))
        {
            ToastService.ShowSuccess("模型健康与熔断状态已同步", "刷新成功");
        }
    }

    private async Task LoadModelHealth()
    {
        try
        {
            var metrics = await Api.GetMetricsAsync();
            if (metrics?.Models != null)
            {
                ModelHealth = metrics.Models.ToList();
                HealthError = null;
            }
        }
        catch (Exception ex)
        {
            // 轮询失败保留上一次数据，但持续显示错误提示（而非静默停留）
            HealthError = "健康状态刷新失败：" + ex.Message;
        }
        finally
        {
            HealthLoading = false;
        }
    }

    private string? HealthError;

    private async Task OverrideCircuit(string modelName, string targetState)
    {
        var (ok, error) = await Api.OverrideCircuitStateAsync(modelName, targetState);
        if (ok)
        {
            Toast(targetState == "Open" ? $"模型 \"{modelName}\" 已强制隔离" : $"模型 \"{modelName}\" 已恢复正常");
            await LoadModelHealth();
        }
        else
        {
            Toast("熔断状态更新失败：" + (error ?? "未知错误"), true);
        }
    }

    private string CircuitBadge(string state) => state.ToLower() switch
    {
        "closed" => "success",
        "open" => "danger",
        "halfopen" => "warning",
        _ => "secondary"
    };

    private string CircuitText(string state) => state.ToLower() switch
    {
        "closed" => "正常 (Closed)",
        "open" => "熔断 (Open)",
        "halfopen" => "探测 (HalfOpen)",
        _ => state
    };

    private async Task LoadModels()
    {
        try
        {
            ModelList = await Api.GetModelsAsync() ?? new();
            await SeedProbeResultsAsync();
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>
    /// 预填"连通状态"列：服务端留痕的最近探活结果（手动 + 后台探活），
    /// 页面刷新/电路重建后不丢。仅预填当前列表里的模型，历史残留条目自然忽略。
    /// </summary>
    private async Task SeedProbeResultsAsync()
    {
        try
        {
            var stored = await Api.GetProbeResultsAsync();
            if (stored is null) return;
            foreach (var (name, s) in stored)
            {
                ProbeResults[name] = new ProbeView(s.Success, s.LatencyMs, s.Message, s.Error, s.TimestampUtc);
            }
        }
        catch (Exception ex)
        {
            // 预填失败不阻塞模型列表加载；列回到"未测试"，手动探活仍可用
            Logger.LogDebug(ex, "Models SeedProbeResults failed");
        }
    }

    /// <summary>探活结果的相对时间（"刚刚 / 5 分钟前 / 3 小时前"），提示结果的时效性。</summary>
    private static string FormatProbeAgo(DateTime timestampUtc)
    {
        var delta = DateTime.UtcNow - timestampUtc;
        if (delta.TotalMinutes < 1) return "刚刚";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} 小时前";
        return $"{(int)delta.TotalDays} 天前";
    }

    private void OpenDiscoverModal()
    {
        // 默认选中已有模型或手动输入
        DiscoverSourceTab = ModelList.Any(m => m.Enabled) ? "existing" : "manual";
        DiscoverSelectedModelName = ModelList.FirstOrDefault(m => m.Enabled)?.Name ?? "";
        DiscoverManualBaseUrl = "";
        DiscoverManualApiKey = "";
        DiscoverManualProtocol = "OpenAI";
        DiscoverError = null;
        DiscoverStep = 1;
        DiscoverFilterTab = "all";
        DiscoveredModels = new();
        DiscoverSelected = new();
        DiscoverSourceBaseUrl = "";
        DiscoverSourceProtocol = "OpenAI";
        DiscoverSourceApiKey = null;
        DiscoverSourceModelName = null;
        DiscoverSearchText = "";
        BulkAddDeployType = "public";
        ShowDiscoverModal = true;
    }

    private void CloseDiscoverModal()
    {
        ShowDiscoverModal = false;
    }

    private void BackToSourceStep()
    {
        DiscoverStep = 1;
    }

    // stepper 第一步仅在 Step 2 时可交互（点击/回车/空格返回上一步）
    private void StepOneClick() { if (DiscoverStep == 2) BackToSourceStep(); }
    private void StepOneKey(KeyboardEventArgs e)
    {
        if (e.Key is "Enter" or " ") StepOneClick();
    }

    private async Task RunDiscoverAsync()
    {
        DiscoverError = null;
        string? apiKey;
        string? modelName = null;
        if (DiscoverSourceTab == "manual")
        {
            if (string.IsNullOrWhiteSpace(DiscoverManualBaseUrl))
            {
                DiscoverError = "请填写 Base URL";
                return;
            }
            DiscoverSourceBaseUrl = DiscoverManualBaseUrl.Trim();
            DiscoverSourceProtocol = DiscoverManualProtocol;
            DiscoverSourceApiKey = string.IsNullOrWhiteSpace(DiscoverManualApiKey) ? null : DiscoverManualApiKey.Trim();
            apiKey = DiscoverSourceApiKey;
            DiscoverSourceModelName = null; // 手动输入的 key 随请求明文传递，无需服务端复制
        }
        else
        {
            var src = ModelList.FirstOrDefault(m => m.Name == DiscoverSelectedModelName) ?? ModelList.FirstOrDefault(m => m.Enabled);
            if (src is null) { DiscoverError = "选择的已有模型已不存在"; return; }
            DiscoverSourceBaseUrl = src.BaseUrl;
            // 凭据零回传：discover 由服务端按 ModelName 解析；批量导入经 ApiKeySourceModel 服务端复制。
            DiscoverSourceApiKey = null;
            apiKey = null;
            modelName = src.Name;
            DiscoverSourceModelName = src.Name;
            if (string.Equals(src.Provider, "gemini", StringComparison.OrdinalIgnoreCase) ||
                src.BaseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                DiscoverSourceProtocol = "Gemini";
            }
            else
            {
                DiscoverSourceProtocol = "OpenAI";
            }
        }

        IsDiscovering = true;
        try
        {
            var (items, err) = await Api.DiscoverModelsAsync(DiscoverSourceBaseUrl, apiKey, DiscoverSourceProtocol, modelName);
            if (err is not null)
            {
                DiscoverError = err;
                return;
            }
            DiscoveredModels = items ?? new();
            if (DiscoveredModels.Count == 0)
            {
                DiscoverError = "上游未返回任何模型（data 为空或响应格式不识别）。";
                return;
            }
            var existing = new HashSet<string>(ModelList.Select(m => m.Name), StringComparer.Ordinal);
            DiscoverSelected = DiscoveredModels
                .Select(m => new DiscoverRow { Model = m, AlreadyExists = existing.Contains(m.Id) })
                .ToList();
            foreach (var r in DiscoverSelected) r.Selected = !r.AlreadyExists;
            DiscoverSearchText = "";
            DiscoverFilterTab = "all";
            DiscoverStep = 2;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    private async Task AddSingleDiscoveredModelAsync(DiscoverRow row)
    {
        if (row.AlreadyExists || row.Added) return;
        var req = new ApiService.CreateModelRequest(
            Name: row.Model.Id,
            BaseUrl: DiscoverSourceBaseUrl,
            ApiKey: DiscoverSourceApiKey,
            ApiKeySourceModel: DiscoverSourceModelName,
            Tier: BulkAddTier,
            MaxContextTokens: BulkAddMaxContextTokens,
            TimeoutSeconds: 120,
            MaxRetries: 0,
            Enabled: bool.Parse(BulkAddEnabled),
            InputPricePerMillion: 0m,
            OutputPricePerMillion: 0m,
            Tags: null,
            Provider: !string.IsNullOrWhiteSpace(row.Model.OwnedBy) ? row.Model.OwnedBy : (DiscoverSourceProtocol == "Gemini" ? "google" : null),
            Family: null,
            CachedInputPricePerMillion: null,
            CacheWriteInputPricePerMillion: null,
            IsLocalOrPrivate: BulkAddDeployType == "local",
            Id: row.Model.Id,
            Weight: 1.0);

        var (ok, err) = await Api.CreateModelAsync(req);
        if (ok)
        {
            row.Added = true;
            row.Selected = false;
            row.AddError = null;
            ToastService.ShowSuccess($"已成功导入模型 {row.Model.Id}", "导入成功");
            await LoadModels();
        }
        else
        {
            row.AddError = err;
            ToastService.ShowError($"导入模型 {row.Model.Id} 失败: {err}", "导入失败");
        }
    }

    private async Task BulkAddSelectedAsync()
    {
        IsBulkAdding = true;
        try
        {
            var existingNames = new HashSet<string>(ModelList.Select(m => m.Name), StringComparer.Ordinal);
            int okCount = 0, failCount = 0;
            foreach (var row in DiscoverSelected.Where(r => r.Selected && !r.AlreadyExists && !r.Added).ToList())
            {
                if (existingNames.Contains(row.Model.Id))
                {
                    row.AlreadyExists = true; row.Selected = false; continue;
                }
                var req = new ApiService.CreateModelRequest(
                    Name: row.Model.Id,
                    BaseUrl: DiscoverSourceBaseUrl,
                    ApiKey: DiscoverSourceApiKey,
                    ApiKeySourceModel: DiscoverSourceModelName,
                    Tier: BulkAddTier,
                    MaxContextTokens: BulkAddMaxContextTokens,
                    TimeoutSeconds: 120,
                    MaxRetries: 0,
                    Enabled: bool.Parse(BulkAddEnabled),
                    InputPricePerMillion: 0m,
                    OutputPricePerMillion: 0m,
                    Tags: null,
                    Provider: !string.IsNullOrWhiteSpace(row.Model.OwnedBy) ? row.Model.OwnedBy : (DiscoverSourceProtocol == "Gemini" ? "google" : null),
                    Family: null,
                    CachedInputPricePerMillion: null,
                    CacheWriteInputPricePerMillion: null,
                    IsLocalOrPrivate: BulkAddDeployType == "local",
                    Id: row.Model.Id,
                    Weight: 1.0);
                var (ok, err) = await Api.CreateModelAsync(req);
                if (ok) { row.Added = true; row.Selected = false; existingNames.Add(row.Model.Id); okCount++; }
                else { row.AddError = err; failCount++; }
            }
            if (okCount > 0)
            {
                ToastService.ShowSuccess($"已添加 {okCount} 个模型{(failCount > 0 ? $"，{failCount} 个失败" : "")}",
                    failCount > 0 ? "部分失败" : "批量添加成功");
                await LoadModels();
            }
            else if (failCount > 0)
            {
                ToastService.ShowError($"批量添加失败 {failCount} 个", "请检查错误");
            }
        }
        finally
        {
            IsBulkAdding = false;
        }
    }

    // ── 表单内拉取上游模型方法 ──
    private async Task FetchUpstreamModelsForForm()
    {
        FormDiscoverError = null;
        if (string.IsNullOrWhiteSpace(Form.BaseUrl))
        {
            FormDiscoverError = "请先填写 BaseUrl";
            return;
        }

        IsFormDiscovering = true;
        try
        {
            string protocol = "OpenAI";
            if (Form.BaseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Form.Provider, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                protocol = "Gemini";
            }

            string? key = string.IsNullOrWhiteSpace(Form.ApiKey) ? null : Form.ApiKey.Trim();
            string? modelName = IsEditing ? EditingName : null;

            var (items, err) = await Api.DiscoverModelsAsync(Form.BaseUrl.Trim(), key, protocol, modelName);
            if (err is not null)
            {
                FormDiscoverError = $"拉取失败: {err}";
                FormDiscoveredModels.Clear();
                return;
            }

            FormDiscoveredModels = items ?? new();
            if (FormDiscoveredModels.Count == 0)
            {
                FormDiscoverError = "上游未返回任何模型（data 为空或响应格式不识别）。";
            }
            else
            {
                ToastService.ShowSuccess($"成功获取到 {FormDiscoveredModels.Count} 个上游模型", "拉取成功");
            }
        }
        finally
        {
            IsFormDiscovering = false;
        }
    }

    private void SelectFormDiscoveredModel(ApiService.DiscoveredModel item)
    {
        Form.Id = item.Id;
        if (string.IsNullOrWhiteSpace(Form.Name))
        {
            Form.Name = item.Id;
        }
        if (string.IsNullOrWhiteSpace(Form.Provider) && !string.IsNullOrWhiteSpace(item.OwnedBy))
        {
            Form.Provider = item.OwnedBy;
        }
        ToastService.ShowInfo($"已选择模型: {item.Id}", "填入成功");
    }

    private void ClearFormDiscovered()
    {
        FormDiscoveredModels.Clear();
        FormDiscoverError = null;
        FormModelSearch = "";
    }

    private async Task ToggleRevealKey(string name)
    {
        if (RevealedKeys.ContainsKey(name))
        {
            RevealedKeys.Remove(name);
            return;
        }

        var (key, error) = await Api.RevealModelApiKeyAsync(name);
        if (key is not null)
            RevealedKeys[name] = key;
        else
            Toast("获取 ApiKey 失败：" + (error ?? "未知错误"), true);
    }

    private async Task ToggleEnabled(ApiService.ModelDto m)
    {
        if (!TogglingModels.Add(m.Name)) return;
        try
        {
            // 只切启用位，其余字段不动（后端按 null=不修改 合并）。
            var req = new ApiService.UpdateModelRequest(
                BaseUrl: null, ApiKey: null, Tier: null,
                MaxContextTokens: null, TimeoutSeconds: null, MaxRetries: null,
                Enabled: !m.Enabled,
                InputPricePerMillion: null, OutputPricePerMillion: null,
                Provider: null, Family: null,
                CachedInputPricePerMillion: null, CacheWriteInputPricePerMillion: null,
                IsLocalOrPrivate: null);
            var (ok, error) = await Api.UpdateModelAsync(m.Name, req);
            if (ok)
            {
                ModelList = ModelList.Select(x => x.Name == m.Name ? x with { Enabled = !x.Enabled } : x).ToList();
            }
            else
            {
                Toast("切换失败：" + (error ?? "未知错误"), true);
            }
        }
        finally
        {
            TogglingModels.Remove(m.Name);
        }
    }

    private void OpenAddModal()
    {
        IsEditing = false;
        EditingName = null;
        CopySourceName = null;
        Form = new ModelForm();
        ShowModalApiKey = true;
        ErrorMsg = null;
        ClearFormDiscovered();
        ShowModal = true;
    }

    /// <summary>
    /// 复制模型：同供应商多模型（key/BaseUrl/计价/超时一致）只需改名称与上游模型 ID。
    /// 名称与 ID 留空强制填写；ApiKey 经 reveal 接口取回预填（列表 DTO 只含遮蔽预览）。
    /// </summary>
    private async Task OpenCopyModal(ApiService.ModelDto m)
    {
        var (key, revealError) = await Api.RevealModelApiKeyAsync(m.Name);
        IsEditing = false;
        EditingName = null;
        CopySourceName = m.Name;
        Form = new ModelForm
        {
            Name = "",
            Id = "",
            BaseUrl = m.BaseUrl,
            ApiKey = key ?? "",
            Tier = m.Tier,
            Provider = m.Provider ?? "",
            Family = m.Family ?? "",
            MaxContextTokens = m.MaxContextTokens,
            TimeoutSeconds = m.TimeoutSeconds,
            MaxRetries = m.MaxRetries,
            Weight = m.Weight,
            EnabledStr = m.Enabled.ToString().ToLower(),
            DeployTypeStr = m.IsLocalOrPrivate ? "local" : "public",
            InputPricePerMillion = m.InputPricePerMillion,
            OutputPricePerMillion = m.OutputPricePerMillion,
            CachedInputPricePerMillion = m.CachedInputPricePerMillion,
            CacheWritePricePerMillion = m.CacheWriteInputPricePerMillion,
            Tags = m.Tags is { Count: > 0 } ? string.Join(", ", m.Tags) : ""
        };
        ShowModalApiKey = true;
        // key 取不回时仍打开弹窗（其余字段已带入），提示手工粘贴。
        ErrorMsg = key is null ? $"获取源模型 ApiKey 失败，请手动填写：{revealError ?? "未知错误"}" : null;
        ClearFormDiscovered();
        ShowModal = true;
    }

    private void OpenEditModal(ApiService.ModelDto m)
    {
        IsEditing = true;
        EditingName = m.Name;
        CopySourceName = null;
        Form = new ModelForm
        {
            Name = m.Name,
            Id = m.Id ?? "",
            BaseUrl = m.BaseUrl,
            ApiKey = "",
            Tier = m.Tier,
            Provider = m.Provider ?? "",
            Family = m.Family ?? "",
            MaxContextTokens = m.MaxContextTokens,
            TimeoutSeconds = m.TimeoutSeconds,
            MaxRetries = m.MaxRetries,
            Weight = m.Weight,
            EnabledStr = m.Enabled.ToString().ToLower(),
            DeployTypeStr = m.IsLocalOrPrivate ? "local" : "public",
            InputPricePerMillion = m.InputPricePerMillion,
            OutputPricePerMillion = m.OutputPricePerMillion,
            CachedInputPricePerMillion = m.CachedInputPricePerMillion,
            CacheWritePricePerMillion = m.CacheWriteInputPricePerMillion,
            Tags = m.Tags is { Count: > 0 } ? string.Join(", ", m.Tags) : ""
        };
        ShowModalApiKey = true;
        ErrorMsg = null;
        ClearFormDiscovered();
        ShowModal = true;
    }

    private void CloseModal()
    {
        if (IsSaving) return; // 保存进行中不允许误关
        ShowModal = false;
        ErrorMsg = null;
        ClearFormDiscovered();
    }

    private void ApplyPreset(string presetKey)
    {
        switch (presetKey.ToLower())
        {
            case "openai":
                Form.Name = "gpt-4o";
                Form.BaseUrl = "https://api.openai.com/v1";
                Form.Tier = "Strong";
                Form.Provider = "openai";
                Form.Family = "gpt-4o";
                Form.MaxContextTokens = 128000;
                Form.InputPricePerMillion = 2.50m;
                Form.OutputPricePerMillion = 10.00m;
                Form.DeployTypeStr = "public";
                break;
            case "deepseek":
                Form.Name = "deepseek-chat";
                Form.BaseUrl = "https://api.deepseek.com/v1";
                Form.Tier = "Cheap";
                Form.Provider = "deepseek";
                Form.Family = "deepseek-v3";
                Form.MaxContextTokens = 64000;
                Form.InputPricePerMillion = 0.14m;
                Form.OutputPricePerMillion = 0.28m;
                Form.DeployTypeStr = "public";
                break;
            case "claude":
                Form.Name = "claude-3-5-sonnet-20241022";
                Form.BaseUrl = "https://api.anthropic.com/v1";
                Form.Tier = "Strong";
                Form.Provider = "anthropic";
                Form.Family = "claude";
                Form.MaxContextTokens = 200000;
                Form.InputPricePerMillion = 3.00m;
                Form.OutputPricePerMillion = 15.00m;
                Form.DeployTypeStr = "public";
                break;
            case "qwen":
                Form.Name = "qwen-max";
                Form.BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
                Form.Tier = "Medium";
                Form.Provider = "aliyun";
                Form.Family = "qwen";
                Form.MaxContextTokens = 32000;
                Form.InputPricePerMillion = 0.30m;
                Form.OutputPricePerMillion = 0.60m;
                Form.DeployTypeStr = "public";
                break;
            case "ollama":
                Form.Name = "llama3.2";
                Form.BaseUrl = "http://localhost:11434/v1";
                Form.Tier = "Cheap";
                Form.Provider = "ollama";
                Form.Family = "local";
                Form.MaxContextTokens = 16384;
                Form.InputPricePerMillion = 0.00m;
                Form.OutputPricePerMillion = 0.00m;
                Form.DeployTypeStr = "local";
                break;
        }
    }

    private async Task SubmitModal()
    {
        ErrorMsg = null;
        if (string.IsNullOrWhiteSpace(Form.BaseUrl))
        {
            ErrorMsg = "BaseUrl 不能为空";
            return;
        }
        if (!IsEditing && string.IsNullOrWhiteSpace(Form.Name))
        {
            ErrorMsg = "名称不能为空";
            return;
        }

        IsSaving = true;
        try
        {
            if (IsEditing)
            {
                var req = new ApiService.UpdateModelRequest(
                    string.IsNullOrWhiteSpace(Form.BaseUrl) ? null : Form.BaseUrl.TrimEnd('/'),
                    string.IsNullOrWhiteSpace(Form.ApiKey) ? null : Form.ApiKey,
                    Form.Tier,
                    Form.MaxContextTokens > 0 ? Form.MaxContextTokens : null,
                    Form.TimeoutSeconds > 0 ? Form.TimeoutSeconds : null,
                    Form.MaxRetries >= 0 ? Form.MaxRetries : null,
                    bool.TryParse(Form.EnabledStr, out var en) ? en : null,
                    Form.InputPricePerMillion >= 0 ? Form.InputPricePerMillion : null,
                    Form.OutputPricePerMillion >= 0 ? Form.OutputPricePerMillion : null,
                    string.IsNullOrWhiteSpace(Form.Provider) ? "" : Form.Provider.Trim(),
                    string.IsNullOrWhiteSpace(Form.Family) ? "" : Form.Family.Trim(),
                    Form.CachedInputPricePerMillion,
                    Form.CacheWritePricePerMillion,
                    Form.DeployTypeStr == "local",
                    ParseTags(Form.Tags),
                    Form.Id.Trim(),
                    Form.Weight);

                var (ok, error) = await Api.UpdateModelAsync(EditingName!, req);
                if (ok)
                {
                    Toast($"模型 \"{EditingName}\" 更新成功");
                    ShowModal = false;
                    await LoadModels();
                }
                else
                {
                    ErrorMsg = "更新失败：" + (error ?? "未知错误");
                }
            }
            else
            {
                var req = new ApiService.CreateModelRequest(
                    Form.Name.Trim(), Form.BaseUrl.Trim().TrimEnd('/'),
                    string.IsNullOrWhiteSpace(Form.ApiKey) ? null : Form.ApiKey,
                    Form.Tier,
                    Form.MaxContextTokens > 0 ? Form.MaxContextTokens : 200000,
                    Form.TimeoutSeconds > 0 ? Form.TimeoutSeconds : 120,
                    Math.Max(0, Form.MaxRetries),
                    bool.Parse(Form.EnabledStr),
                    Math.Max(0, Form.InputPricePerMillion),
                    Math.Max(0, Form.OutputPricePerMillion),
                    ParseTags(Form.Tags),
                    string.IsNullOrWhiteSpace(Form.Provider) ? null : Form.Provider.Trim(),
                    string.IsNullOrWhiteSpace(Form.Family) ? null : Form.Family.Trim(),
                    Form.CachedInputPricePerMillion,
                    Form.CacheWritePricePerMillion,
                    Form.DeployTypeStr == "local",
                    Form.Id.Trim(),
                    Form.Weight);

                var (ok, error) = await Api.CreateModelAsync(req);
                if (ok)
                {
                    Toast($"模型 \"{Form.Name}\" 添加成功");
                    ShowModal = false;
                    await LoadModels();
                }
                else
                {
                    ErrorMsg = "创建失败：" + (error ?? "未知错误");
                }
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task ProbeConnection(string name)
    {
        ProbingModels.Add(name);
        StateHasChanged();
        try
        {
            var res = await Api.TestModelConnectionAsync(name);
            if (res != null)
            {
                ProbeResults[name] = new ProbeView(res.Success, res.LatencyMs, res.Message, res.Error, DateTime.UtcNow);
                if (res.Success)
                {
                    ToastService.ShowSuccess($"端点「{name}」探活成功，延迟: {res.LatencyMs}ms", "探活成功");
                }
                else
                {
                    ToastService.ShowError($"端点「{name}」探活失败: {res.Error}", "探活失败");
                }
            }
        }
        catch (Exception ex)
        {
            // 探测请求本身失败（网络/序列化）：以失败结果展示，而不是无任何反馈
            ProbeResults[name] = new ProbeView(false, 0, "探测请求失败", ex.Message, DateTime.UtcNow);
            ToastService.ShowError($"端点「{name}」探测异常: {ex.Message}", "探活异常");
        }
        finally
        {
            ProbingModels.Remove(name);
            try { StateHasChanged(); } catch (ObjectDisposedException) { } // circuit 已拆除时静默退出
        }
    }

    private async Task Delete(string name)
    {
        var (ok, error) = await Api.DeleteModelAsync(name);
        if (!ok)
        {
            Toast("删除失败：" + (error ?? "未知错误"), true);
            return;
        }
        RevealedKeys.Remove(name);
        Toast($"模型 \"{name}\" 已删除");
        await LoadModels();
    }

    private void Toast(string msg, bool isError = false)
    {
        if (isError)
            ToastService.ShowError(msg, "操作失败");
        else
            ToastService.ShowSuccess(msg, "操作成功");
    }

    private static string Truncate(string? s, int len) => s != null && s.Length > len ? s[..len] + "..." : s ?? "-";

    /// <summary>逗号分隔的能力标签文本 → 去空去重列表；全空返回空列表（允许清空 Tags）。</summary>
    private static List<string> ParseTags(string? tagsText)
        => (tagsText ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private class ModelForm
    {
        public string Name { get; set; } = "";
        // 上游真实模型 id：留空回退 Name。同一模型多账号/多供应商时，路由名不同、此 id 相同。
        public string Id { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string? ApiKey { get; set; }
        public string Tier { get; set; } = "Medium";
        public string Provider { get; set; } = "";
        public string Family { get; set; } = "";
        public int MaxContextTokens { get; set; } = 200000;
        public int TimeoutSeconds { get; set; } = 120;
        public int MaxRetries { get; set; } = 0;
        public double Weight { get; set; } = 1.0;
        // 部署类型：public=公有云，local=本地/私有（与列表 ☁️/🏠 徽标对应）。
        public string DeployTypeStr { get; set; } = "public";
        public string EnabledStr { get; set; } = "true";
        public decimal InputPricePerMillion { get; set; } = 0;
        public decimal OutputPricePerMillion { get; set; } = 0;
        public decimal? CachedInputPricePerMillion { get; set; }
        public decimal? CacheWritePricePerMillion { get; set; }
        public string Tags { get; set; } = "";
    }
}
