using System.Text.Json;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 级联自校验工具：从原始响应抽取答案文本、构造自校验请求、判定置信度。
/// 用于 Cheap→Strong 升级判定（评审缺口①）：Cheap 答完做一次自校验，低置信则升级 Strong 重答。
/// </summary>
public static class ResponseConfidenceChecker
{
    /// <summary>自校验 prompt 常量。要求模型只回 CONFIDENT / UNCERTAIN。</summary>
    public const string DefaultSelfVerifyPrompt =
        "请复核上面助手给出的答案是否正确且完整。只回答一个词：CONFIDENT（确信正确）或 UNCERTAIN（不确定或有错）。不要解释。";

    /// <summary>
    /// 从 RawChatResponse.Body 一次解析 choices[0] 的 (content, finishReason)。
    /// 容错：JSON 损坏/缺字段时对应分量返回空串，不抛异常。供质量信号提取与置信文本抽取共享单次解析。
    /// </summary>
    public static (string Content, string FinishReason) ExtractAssistantContentAndFinishReason(RawChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.Body)) return (string.Empty, string.Empty);

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return (string.Empty, string.Empty);

            var firstChoice = choices[0];

            // finish_reason 独立于 message，可能存在于一者缺失的场景，分别解析。
            string finishReason = string.Empty;
            if (firstChoice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString() ?? string.Empty;

            if (!firstChoice.TryGetProperty("message", out var message))
                return (string.Empty, finishReason);
            if (!message.TryGetProperty("content", out var content))
                return (string.Empty, finishReason);

            // content 可能是 string 或多模态数组。复用 ChatMessage.GetText 的语义：string 直取，数组拼 text 段。
            string text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => ConcatTextArray(content),
                _ => string.Empty
            };
            return (text, finishReason);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// 从 RawChatResponse.Body 解析 choices[0].message.content 文本。
    /// 容错：JSON 损坏/无 choices/content 为多模态数组时返回空串，不抛异常。
    /// </summary>
    public static string ExtractAssistantText(RawChatResponse response)
        => ExtractAssistantContentAndFinishReason(response).Content;

    private static string ConcatTextArray(JsonElement content)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.ValueEquals("text")
                && item.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                sb.Append(textEl.GetString());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 构造自校验请求：[user 原问题, assistant Cheap 答案, user 校验 prompt]。
    /// 保留原请求的 sampling 参数（Temperature 等）由调用方决定是否降参；此处仅构造消息链。
    /// </summary>
    public static ChatRequest BuildVerificationRequest(ChatRequest original, string cheapAnswer, string verifyPrompt)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(verifyPrompt);

        // 取原请求最后一条 user 消息作为待复核问题；缺失则用全部消息文本拼接兜底。
        string question = GetLastUserText(original);

        return new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", question),
                ChatMessage.FromText("assistant", cheapAnswer),
                ChatMessage.FromText("user", verifyPrompt)
            },
            // 自校验要确定性答案，强制低温度（不覆盖 MaxTokens 以保留调用方控制）。
            Temperature = 0
        };
    }

    /// <summary>
    /// 判定自校验响应是否表示自信。解析响应文本：含 "CONFIDENT" 视为自信，否则（UNCERTAIN/异常文本）视为不自信触发升级。
    /// 容错优先升级：宁可多花一次 Strong 调用，不漏质量隐患。
    /// </summary>
    public static bool IsConfident(ChatResponse verifyResponse)
    {
        ArgumentNullException.ThrowIfNull(verifyResponse);

        var text = GetFirstChoiceText(verifyResponse);
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 大小写不敏感匹配，且要求 CONFIDENT 与 UNCERTAIN 区分（避免 "NOT CONFIDENT" 被误判）。
        // 先判 UNCERTAIN：模型若回 "I am UNCERTAIN" 不应被 CONFIDENT 子串误命中。
        var upper = text.Trim().ToUpperInvariant();
        if (upper.Contains("UNCERTAIN")) return false;
        return upper.Contains("CONFIDENT");
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

    private static string GetFirstChoiceText(ChatResponse response)
    {
        if (response.Choices is null || response.Choices.Count == 0) return string.Empty;
        var choice = response.Choices[0];
        return choice?.Message?.GetText() ?? string.Empty;
    }
}
