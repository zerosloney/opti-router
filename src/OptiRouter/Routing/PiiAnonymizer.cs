using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// PiiMap 保存敏感占位符与原始文本的映射关系，用于回答后的还原。
/// </summary>
public sealed class PiiMap
{
    private readonly Dictionary<string, string> _placeholderToOriginal = new(StringComparer.Ordinal);

    public bool HasSensitiveData => _placeholderToOriginal.Count > 0;

    public void Add(string placeholder, string original)
    {
        _placeholderToOriginal[placeholder] = original;
    }

    public string Restore(string text)
    {
        if (string.IsNullOrEmpty(text) || _placeholderToOriginal.Count == 0)
            return text;

        string restored = text;
        foreach (var (placeholder, original) in _placeholderToOriginal)
        {
            restored = restored.Replace(placeholder, original, StringComparison.Ordinal);
        }
        return restored;
    }
}

/// <summary>
/// PII 敏感数据脱敏与反向还原引擎：
/// 识别手机号、电子邮箱、身份证号、银行卡号与 IP 地址，并在发送给外部大模型前自动替换为具名占位符；
/// 模型作答后再自动反向还原。
/// </summary>
public static class PiiAnonymizer
{
    private static long _phoneCount;
    private static long _emailCount;
    private static long _idCardCount;
    private static long _cardCount;
    private static long _ipCount;

    public static (long Phone, long Email, long IdCard, long CreditCard, long Ip, long Total) GetStats()
    {
        long p = Interlocked.Read(ref _phoneCount);
        long e = Interlocked.Read(ref _emailCount);
        long id = Interlocked.Read(ref _idCardCount);
        long c = Interlocked.Read(ref _cardCount);
        long ip = Interlocked.Read(ref _ipCount);
        return (p, e, id, c, ip, p + e + id + c + ip);
    }
    // 手机号 (中国手机号及带国际区号格式)
    private static readonly Regex PhoneRegex = new(
        @"(?:\+?86\s*)?(?:1[3-9]\d{9}|\b1[3-9]\d[-\s]\d{4}[-\s]\d{4}\b)",
        RegexOptions.Compiled);

    // 电子邮箱
    private static readonly Regex EmailRegex = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    // 18 位身份证号
    private static readonly Regex IdCardRegex = new(
        @"\b[1-9]\d{5}(?:18|19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dX]\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 16 位银行/信用卡号
    private static readonly Regex CreditCardRegex = new(
        @"\b(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|6(?:011|5[0-9]{2})[0-9]{12}|3[47][0-9]{13})\b",
        RegexOptions.Compiled);

    // IPv4 地址
    private static readonly Regex IpRegex = new(
        @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// 对 ChatRequest 中的消息文本进行敏感数据脱敏。
    /// 纯文本 content 按整段脱敏后用 <see cref="ChatMessage.FromText"/> 重建；
    /// 多模态数组 content 仅脱敏 <c>{type:"text",text:...}</c> 片段，<c>image_url</c> 等非文本部分原样保留——
    /// 否则开启 PII 脱敏会让视觉请求的图片被静默丢弃。
    /// </summary>
    public static (ChatRequest SanitizedRequest, PiiMap PiiMap, bool ContainsPii) AnonymizeRequest(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null || request.Messages.Count == 0)
            return (request, new PiiMap(), false);

        var piiMap = new PiiMap();
        var anonymizer = new SegmentAnonymizer(piiMap);
        var sanitizedMessages = new List<ChatMessage>(request.Messages.Count);

        foreach (var msg in request.Messages)
        {
            if (msg is null) continue;

            // 无 content（如纯 tool 调用）：不动。
            if (msg.Content is not { } content)
            {
                sanitizedMessages.Add(msg);
                continue;
            }

            // 纯文本：脱敏后 FromText 重建（保持既有行为）。
            if (content.ValueKind == JsonValueKind.String)
            {
                string original = content.GetString() ?? string.Empty;
                string anonymized = anonymizer.Anonymize(original);
                sanitizedMessages.Add(anonymized != original
                    ? ChatMessage.FromText(msg.Role, anonymized)
                    : msg);
                continue;
            }

            // 多模态数组：逐片段脱敏文本部分，保留图像/音频等结构。
            if (content.ValueKind == JsonValueKind.Array)
            {
                var (rebuilt, changed) = AnonymizeArrayContent(content, anonymizer);
                sanitizedMessages.Add(changed
                    ? new ChatMessage { Role = msg.Role, Content = rebuilt }
                    : msg);
                continue;
            }

            // 其他形态（object/number 等，罕见）：不动。
            sanitizedMessages.Add(msg);
        }

        return (request with { Messages = sanitizedMessages }, piiMap, anonymizer.ContainsPii);
    }

    /// <summary>
    /// 脱敏多模态数组 content：克隆每个元素，仅替换文本片段的 text 字段，其余（image_url 等）原样保留。
    /// 用 <see cref="JsonElement"/> API 做无歧义的类型/取值检测，<see cref="JsonObject"/> 仅用于克隆与写入 text。
    /// </summary>
    /// <returns>重建后的数组 JsonElement 与"是否发生改动"标志；未改动时返回 (null, false) 以避免无谓重序列化。</returns>
    private static (JsonElement? Rebuilt, bool Changed) AnonymizeArrayContent(JsonElement array, SegmentAnonymizer anonymizer)
    {
        bool changed = false;
        var rebuilt = new JsonArray();
        foreach (var item in array.EnumerateArray())
        {
            // 用 GetRawText → JsonNode 克隆，完整保留未知结构（image_url url、detail 等）。
            JsonNode? node = JsonNode.Parse(item.GetRawText());

            // 文本片段检测走 JsonElement（GetString/ValueEquals 语义明确），并保持在同一 if 链内，
            // 让 textEl 的 out var 定赋可被编译器追踪。
            if (node is JsonObject obj
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.ValueEquals("text")
                && item.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                string original = textEl.GetString() ?? string.Empty;
                string anonymized = anonymizer.Anonymize(original);
                if (!string.Equals(anonymized, original, StringComparison.Ordinal))
                {
                    obj["text"] = anonymized;
                    changed = true;
                }
            }
            rebuilt.Add(node);
        }

        return changed ? (JsonSerializer.SerializeToElement(rebuilt), true) : (null, false);
    }

    /// <summary>
    /// 单次请求范围内的脱敏器：持有本次请求的占位符计数器与 <see cref="PiiMap"/>，
    /// 让纯文本与多模态各 text 片段共享同一套唯一占位符命名空间，反向还原无歧义。
    /// </summary>
    private sealed class SegmentAnonymizer
    {
        private readonly PiiMap _map;
        private int _idCard, _phone, _email, _card, _ip;
        public bool ContainsPii { get; private set; }

        public SegmentAnonymizer(PiiMap map) => _map = map;

        public string Anonymize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            string anonymized = text;
            anonymized = IdCardRegex.Replace(anonymized, m => Annotate(m, ref _idCard, "ID_CARD", ref _idCardCount));
            anonymized = PhoneRegex.Replace(anonymized, m => Annotate(m, ref _phone, "PHONE", ref _phoneCount));
            anonymized = EmailRegex.Replace(anonymized, m => Annotate(m, ref _email, "EMAIL", ref _emailCount));
            anonymized = CreditCardRegex.Replace(anonymized, m => Annotate(m, ref _card, "CARD", ref _cardCount));
            anonymized = IpRegex.Replace(anonymized, m => Annotate(m, ref _ip, "IP", ref _ipCount));
            return anonymized;
        }

        private string Annotate(Match match, ref int perRequestCounter, string kind, ref long globalCounter)
        {
            ContainsPii = true;
            Interlocked.Increment(ref globalCounter);
            string placeholder = $"[PII_{kind}_{++perRequestCounter}]";
            _map.Add(placeholder, match.Value);
            return placeholder;
        }
    }

    /// <summary>
    /// 将模型生成的回答反向还原脱敏占位符。
    /// </summary>
    public static string DeanonymizeText(string responseText, PiiMap piiMap)
    {
        ArgumentNullException.ThrowIfNull(piiMap);
        return piiMap.Restore(responseText);
    }
}
