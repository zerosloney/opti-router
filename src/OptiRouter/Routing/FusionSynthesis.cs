using System.Text;
using System.Text.Json;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 融合路由合成工具：构造 analyst 分析请求、outer 最终答案请求，并解析 analyst 的结构化 JSON。
/// 参照 OpenRouter Fusion Router：panel 并行作答 → analyst 产结构化分析（不写答案）→ outer 写最终答案。
/// 纯静态、无副作用，供 <c>ProxyOrchestrator.TryFusionRouterAsync</c> 复用。
/// </summary>
public static class FusionSynthesis
{
    /// <summary>
    /// analyst prompt 常量。要求模型阅读问题与全部 panel 回答，只输出指定字段的 JSON。
    /// 字段与 <see cref="FusionAnalysis"/> 对应（snake_case），供 <see cref="ParseAnalysis"/> 解析。
    /// </summary>
    public const string DefaultAnalystPrompt =
        "你是分析师。下面是一道用户问题与多个模型（panel）的独立回答。请综合比较，只输出一个 JSON 对象，字段为：" +
        "consensus（所有或多数模型一致认同的结论）、contradictions（模型间的矛盾或分歧）、" +
        "gaps（所有模型都未覆盖的盲点）、unique_insights（个别模型提供、其他模型遗漏的独到见解）、" +
        "recommendation（综合后建议最终答案的方向）。不要输出 JSON 之外的任何文字。";

    /// <summary>
    /// outer prompt 常量。要求模型基于 analyst 分析撰写最终答案。
    /// </summary>
    public const string DefaultOuterPrompt =
        "你是最终执笔人。下面是一份多模型分析（含共识、矛盾、缺口、独特洞察与建议）。" +
        "请基于该分析，结合原始问题，给出一个准确、完整、自洽的最终答案。不要复述分析过程，直接作答。";

    /// <summary>
    /// 构造 analyst 请求：[user 原问题, user 分析指令（内嵌全部 panel 回答）]。
    /// Temperature 固定为 <paramref name="temperature"/>（融合路由默认 0 保确定性）。
    /// 原请求的 temperature 若显式设置则优先，否则用传入默认值。
    /// </summary>
    public static ChatRequest BuildAnalystRequest(
        ChatRequest original,
        IReadOnlyList<(string Model, string Text)> panelAnswers,
        string analystPrompt,
        double temperature,
        bool requestJsonFormat = false)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(analystPrompt);

        string question = GetLastUserText(original);
        string instruction = BuildAnalystInstruction(analystPrompt, panelAnswers);

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", question),
                ChatMessage.FromText("user", instruction)
            },
            Temperature = original.Temperature ?? temperature,
            MaxTokens = null,
            ExtensionData = original.ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(original.ExtensionData, StringComparer.Ordinal)
        };

        // P2：解析失败重试时请求上游强制 JSON 输出（response_format 经 ExtensionData 透传，
        // 上游不支持时忽略该字段，行为回退为普通输出）。
        if (requestJsonFormat)
        {
            request = request with
            {
                ExtensionData = WithJsonFormat(request.ExtensionData)
            };
        }

        return request;
    }

    /// <summary>
    /// P2：analyst 解析失败且重试仍失败时的软降级——用 analyst 原始文本作 Recommendation，
    /// 保住已付 panel 成本，不回退串行。其余字段留空，由 outer 读 Recommendation 写答案。
    /// </summary>
    public static FusionAnalysis BuildFallbackAnalysis(string rawText)
    {
        return new FusionAnalysis
        {
            Consensus = string.Empty,
            Contradictions = string.Empty,
            Gaps = string.Empty,
            UniqueInsights = string.Empty,
            Recommendation = rawText
        };
    }

    private static IDictionary<string, JsonElement>? WithJsonFormat(IDictionary<string, JsonElement>? existing)
    {
        var dict = existing is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal);
        using var doc = JsonDocument.Parse("{\"type\":\"json_object\"}");
        dict["response_format"] = doc.RootElement.Clone();
        return dict;
    }

    /// <summary>
    /// 构造 outer 请求：保留原请求完整消息链，追加一条 user 消息（分析摘要 + outer 指令）。
    /// MaxTokens 设为 <paramref name="maxOutputTokens"/>（融合路由最终答案上限）。
    /// </summary>
    public static ChatRequest BuildOuterRequest(
        ChatRequest original,
        FusionAnalysis analysis,
        string outerPrompt,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(outerPrompt);

        var messages = new List<ChatMessage>();
        if (original.Messages is not null)
            messages.AddRange(original.Messages);

        messages.Add(ChatMessage.FromText("user", BuildOuterInstruction(outerPrompt, analysis)));

        return new ChatRequest
        {
            Messages = messages,
            Temperature = original.Temperature,
            MaxTokens = maxOutputTokens > 0 ? maxOutputTokens : null,
            ExtensionData = original.ExtensionData is null
                ? null
                : new Dictionary<string, JsonElement>(original.ExtensionData, StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// 从 analyst 的 RawChatResponse.Body 解析结构化分析。容错：JSON 缺失/损坏/被代码围栏包裹时返回 null，不抛异常。
    /// </summary>
    public static FusionAnalysis? ParseAnalysis(RawChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string text = ResponseConfidenceChecker.ExtractAssistantText(response);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            // 使用 JSON AST 修复器自动剥离代码围栏、闲聊文本并修补断尾语法
            string json = JsonAstRepairer.RepairJson(text);
            using var doc = JsonDocument.Parse(json);
            return new FusionAnalysis
            {
                Consensus = ReadString(doc.RootElement, "consensus"),
                Contradictions = ReadString(doc.RootElement, "contradictions"),
                Gaps = ReadString(doc.RootElement, "gaps"),
                UniqueInsights = ReadString(doc.RootElement, "unique_insights"),
                Recommendation = ReadString(doc.RootElement, "recommendation")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildAnalystInstruction(string analystPrompt, IReadOnlyList<(string Model, string Text)> panelAnswers)
    {
        var sb = new StringBuilder();
        // Top-loaded static instruction prefix for Automatic Prefix Caching (APC)
        sb.AppendLine("[SYSTEM_PREFIX_INSTRUCTION: ANALYST_SYNTHESIS_V1]");
        sb.AppendLine(analystPrompt);
        sb.AppendLine();
        sb.AppendLine("## Panel 回答 (已压缩蒸馏)");
        for (int i = 0; i < panelAnswers.Count; i++)
        {
            var (model, text) = panelAnswers[i];
            sb.Append("【模型 ").Append(i + 1);
            if (!string.IsNullOrWhiteSpace(model))
                sb.Append("：").Append(model);
            sb.AppendLine("】");
            
            string compressed = CompressPanelText(text);
            sb.AppendLine(string.IsNullOrWhiteSpace(compressed) ? "（无有效回答）" : compressed);
            sb.AppendLine();
        }
        sb.Append("请只输出 JSON：{\"consensus\":\"...\",\"contradictions\":\"...\",\"gaps\":\"...\",\"unique_insights\":\"...\",\"recommendation\":\"...\"}");
        return sb.ToString();
    }

    private static string BuildOuterInstruction(string outerPrompt, FusionAnalysis analysis)
    {
        var sb = new StringBuilder();
        // Top-loaded static instruction prefix for Automatic Prefix Caching (APC)
        sb.AppendLine("[SYSTEM_PREFIX_INSTRUCTION: OUTER_SYNTHESIS_V1]");
        sb.AppendLine(outerPrompt);
        sb.AppendLine();
        sb.AppendLine("## 分析摘要");
        sb.AppendLine("- 共识：" + (string.IsNullOrWhiteSpace(analysis.Consensus) ? "（无）" : analysis.Consensus));
        sb.AppendLine("- 矛盾：" + (string.IsNullOrWhiteSpace(analysis.Contradictions) ? "（无）" : analysis.Contradictions));
        sb.AppendLine("- 缺口：" + (string.IsNullOrWhiteSpace(analysis.Gaps) ? "（无）" : analysis.Gaps));
        sb.AppendLine("- 独特洞察：" + (string.IsNullOrWhiteSpace(analysis.UniqueInsights) ? "（无）" : analysis.UniqueInsights));
        sb.AppendLine("- 建议方向：" + (string.IsNullOrWhiteSpace(analysis.Recommendation) ? "（无）" : analysis.Recommendation));
        return sb.ToString();
    }

    /// <summary>
    /// 蒸馏压缩 Panel 回答：去除开场白/问候语、重复的多余空行与无意义填充，显著降低 Analyst 与 Outer 的 Input Token 消耗。
    /// </summary>
    public static string CompressPanelText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        var lines = rawText.Split('\n');
        var sb = new StringBuilder();
        bool isFirstLine = true;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // 跳过常见人工智能开场问候语
            if (isFirstLine && (trimmed.StartsWith("你好", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("Hello", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("当然", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("没问题", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("作为AI", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("As an AI", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(trimmed))
            {
                sb.AppendLine(trimmed);
                isFirstLine = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            int firstNewline = t.IndexOf('\n');
            if (firstNewline > 0)
                t = t.Substring(firstNewline + 1);
            int closing = t.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0)
                t = t.Substring(0, closing);
        }
        return t.Trim();
    }

    private static string ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return string.Empty;
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
    }

    private static string GetLastUserText(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return string.Empty;

        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg is not null && msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var text = msg.GetText();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }

        return request.Messages[^1]?.GetText() ?? string.Empty;
    }
}
