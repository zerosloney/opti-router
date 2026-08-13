using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 模型能力过滤策略：根据请求内容检测所需能力（vision/tool-use/json-mode），
/// 排除 Tags 不含所需能力的候选模型。策略链最前——能力不匹配的直接砍掉，后续策略不再考虑。
/// </summary>
/// <remarks>
/// 能力标注复用 <see cref="ModelEndpointOptions.Tags"/> 字符串列表，语义约定：
/// <list type="bullet">
/// <item>"vision" — 支持图片输入（多模态 image_url）</item>
/// <item>"tool-use" — 支持 function/tool calling</item>
/// <item>"json-mode" — 支持 response_format json_object</item>
/// </list>
/// 请求侧能力检测：
/// <list type="bullet">
/// <item>vision — <see cref="ChatMessage.Content"/> 数组含 type=="image_url" 项</item>
/// <item>tool-use — <see cref="ChatRequest.ExtensionData"/> 含 tools 非空数组</item>
/// <item>json-mode — <see cref="ChatRequest.ExtensionData"/> 含 response_format.type=="json_object"</item>
/// </list>
/// 无能力需求（required 空）时透传，不破坏无能力标注的现有配置。
/// 过滤后为空时返回空候选。能力要求是正确性硬约束，不能把请求发送给已知不支持该能力的模型。
/// </remarks>
public sealed class CapabilityFilterPolicy : IRouterPolicy
{
    public PolicyGroup Group => PolicyGroup.Filter;

    /// <summary>模型 Tags 中标识支持视觉输入的标签。委托 <see cref="ModelCapabilities.Vision"/>。</summary>
    public const string VisionTag = ModelCapabilities.Vision;

    /// <summary>模型 Tags 中标识支持工具调用的标签。委托 <see cref="ModelCapabilities.ToolUse"/>。</summary>
    public const string ToolUseTag = ModelCapabilities.ToolUse;

    /// <summary>模型 Tags 中标识支持 json-mode 的标签。委托 <see cref="ModelCapabilities.JsonMode"/>。</summary>
    public const string JsonModeTag = ModelCapabilities.JsonMode;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableCapabilityFilter)
        {
            return previous.Append("capability-filter", "disabled");
        }

        var required = DetectRequiredCapabilities(context.Request);
        if (required.Count == 0)
        {
            return previous.Append("capability-filter", "no-requirements");
        }

        var filtered = previous.Candidates
            .Where(m => HasAllTags(m, required))
            .ToList();

        if (filtered.Count == 0)
        {
            string noCandidateReason = $"required {string.Join("/", required)}; no eligible candidate supports all required capabilities";
            var rejected = previous with { Candidates = Array.Empty<ModelEndpointOptions>() };
            return rejected.Append("capability-filter", noCandidateReason);
        }

        if (filtered.Count == previous.Candidates.Count)
        {
            return previous.Append("capability-filter", $"all candidates match {string.Join("/", required)}");
        }

        int removed = previous.Candidates.Count - filtered.Count;
        string reason = $"required {string.Join("/", required)}, removed {removed}, {filtered.Count} remaining";
        var filteredDecision = previous with { Candidates = filtered };
        return filteredDecision.Append("capability-filter", reason);
    }

    /// <summary>
    /// 从请求内容检测所需能力标签集合。无需求返回空集。
    /// </summary>
    internal static HashSet<string> DetectRequiredCapabilities(ChatRequest request)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // vision：任一消息 content 数组含 image_url 项。
        if (RequestContainsImage(request))
            required.Add(VisionTag);

        // tool-use / json-mode：读 ExtensionData 透传字段。
        if (request.ExtensionData is { } ext)
        {
            if (HasNonEmptyToolsArray(ext))
                required.Add(ToolUseTag);
            if (IsJsonObjectMode(ext))
                required.Add(JsonModeTag);
        }

        return required;
    }

    private static bool RequestContainsImage(ChatRequest request)
    {
        if (request.Messages is null) return false;
        foreach (var msg in request.Messages)
        {
            if (msg.Content is not { } el) continue;
            if (el.ValueKind != JsonValueKind.Array) continue;

            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && typeEl.ValueEquals("image_url"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasNonEmptyToolsArray(IDictionary<string, JsonElement> ext)
    {
        if (!ext.TryGetValue("tools", out var toolsEl)) return false;
        return toolsEl.ValueKind == JsonValueKind.Array && toolsEl.GetArrayLength() > 0;
    }

    private static bool IsJsonObjectMode(IDictionary<string, JsonElement> ext)
    {
        if (!ext.TryGetValue("response_format", out var rfEl)) return false;
        return rfEl.ValueKind == JsonValueKind.Object
            && rfEl.TryGetProperty("type", out var typeEl)
            && typeEl.ValueKind == JsonValueKind.String
            && typeEl.ValueEquals("json_object");
    }

    private static bool HasAllTags(ModelEndpointOptions model, IReadOnlySet<string> required)
    {
        if (model.Tags is null || model.Tags.Count == 0) return false;
        // 模型 Tags 转集合一次（Tags 通常 <10 项，每策略调用一次，O(n*m) 可忽略）。
        var tagSet = new HashSet<string>(model.Tags, StringComparer.OrdinalIgnoreCase);
        foreach (var req in required)
        {
            if (!tagSet.Contains(req))
                return false;
        }
        return true;
    }
}
