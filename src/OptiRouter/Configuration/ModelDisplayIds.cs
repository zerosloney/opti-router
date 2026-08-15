namespace OptiRouter.Configuration;

/// <summary>
/// 模型对外显示 ID 的统一格式与解析：<c>{供应商}/{上游真实模型 Id}</c>，如 "deepseek/deepseek-chat"。
/// /v1/models 以此格式展示；请求 <c>model</c> 用此格式时由 <see cref="Resolve"/> 自动解析为端点，
/// 上游收到的仍是真实模型 Id（<see cref="ModelEndpointOptions.UpstreamModelId"/>）。
/// 同供应商同真实模型多端点（多 Key）时，后续端点的显示 ID 追加 " #2"、" #3"。
/// </summary>
public static partial class ModelDisplayIds
{
    /// <summary>
    /// 端点的有效供应商标识：显式 <c>Provider</c> 配置优先，缺省从 BaseUrl 推断，再缺省 "unknown"。
    /// 与 /v1/models 的 provider 字段一致。
    /// </summary>
    public static string EffectiveProvider(ModelEndpointOptions model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!string.IsNullOrWhiteSpace(model.Provider))
        {
            return model.Provider.Trim();
        }

        var inferred = ProviderInference.Infer(model.BaseUrl);
        return string.IsNullOrWhiteSpace(inferred) ? "unknown" : inferred;
    }

    /// <summary>
    /// 基础显示 ID："{供应商}/{真实模型 Id}"。不含多端点去重序号。
    /// </summary>
    public static string BaseDisplayId(ModelEndpointOptions model) =>
        $"{EffectiveProvider(model)}/{model.UpstreamModelId.Trim()}";

    /// <summary>
    /// 按列表顺序计算各端点的显示 ID；同基础 ID 重复时，第 2 个起追加 " #2"、" #3"。
    /// </summary>
    public static IReadOnlyList<string> Compute(IReadOnlyList<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var bases = models.Select(BaseDisplayId).ToList();
        var counts = bases
            .GroupBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = new string[models.Count];
        for (int i = 0; i < bases.Count; i++)
        {
            if (counts.TryGetValue(bases[i], out var count) && count > 1)
            {
                taken.TryGetValue(bases[i], out var seen);
                taken[bases[i]] = seen + 1;
                result[i] = seen == 0 ? bases[i] : $"{bases[i]} #{seen + 1}";
            }
            else
            {
                result[i] = bases[i];
            }
        }

        return result;
    }

    /// <summary>
    /// 解析请求的 <c>model</c> 字段为匹配端点列表。匹配优先级：
    /// 路由名 Name（唯一，含自动生成名）→ 显示 ID（"{供应商}/{Id}"，带或不带 " #N" 序号，
    /// 无序号时返回该供应商该模型的全部端点）→ 裸上游 Id（任意供应商的全部提供方）。
    /// 全部忽略大小写；无匹配返回空列表。
    /// </summary>
    public static List<ModelEndpointOptions> Resolve(IReadOnlyList<ModelEndpointOptions> models, string requestedModel)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return new List<ModelEndpointOptions>();
        }

        var requested = requestedModel.Trim();

        var byName = models
            .Where(m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count > 0)
        {
            return byName;
        }

        // 带 " #N" 序号的显示 ID 精确命中被编号的那个端点；
        // 无序号的基础显示 ID "{供应商}/{Id}" 命中提供该模型的全部端点（多 Key 场景择优/降级）。
        if (NumberedSuffixRegex().IsMatch(requested))
        {
            var displayIds = Compute(models);
            var byNumberedDisplay = models
                .Where((m, i) => string.Equals(displayIds[i], requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byNumberedDisplay.Count > 0)
            {
                return byNumberedDisplay;
            }
        }
        else
        {
            var byDisplay = models
                .Where(m => string.Equals(BaseDisplayId(m), requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byDisplay.Count > 0)
            {
                return byDisplay;
            }
        }

        return models
            .Where(m => string.Equals(m.UpstreamModelId.Trim(), requested, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+#\d+$")]
    private static partial System.Text.RegularExpressions.Regex NumberedSuffixRegex();
}
