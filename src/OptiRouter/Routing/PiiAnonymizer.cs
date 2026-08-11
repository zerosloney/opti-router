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
    /// </summary>
    public static (ChatRequest SanitizedRequest, PiiMap PiiMap, bool ContainsPii) AnonymizeRequest(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null || request.Messages.Count == 0)
            return (request, new PiiMap(), false);

        var piiMap = new PiiMap();
        var sanitizedMessages = new List<ChatMessage>(request.Messages.Count);
        bool containsPii = false;

        int phoneCount = 0, emailCount = 0, idCardCount = 0, cardCount = 0, ipCount = 0;

        foreach (var msg in request.Messages)
        {
            if (msg is null) continue;

            string originalText = msg.GetText();
            if (string.IsNullOrEmpty(originalText))
            {
                sanitizedMessages.Add(msg);
                continue;
            }

            string anonymizedText = originalText;

            // 1. 身份证号脱敏
            anonymizedText = IdCardRegex.Replace(anonymizedText, match =>
            {
                containsPii = true;
                Interlocked.Increment(ref _idCardCount);
                string placeholder = $"[PII_ID_CARD_{++idCardCount}]";
                piiMap.Add(placeholder, match.Value);
                return placeholder;
            });

            // 2. 手机号脱敏
            anonymizedText = PhoneRegex.Replace(anonymizedText, match =>
            {
                containsPii = true;
                Interlocked.Increment(ref _phoneCount);
                string placeholder = $"[PII_PHONE_{++phoneCount}]";
                piiMap.Add(placeholder, match.Value);
                return placeholder;
            });

            // 3. 邮箱脱敏
            anonymizedText = EmailRegex.Replace(anonymizedText, match =>
            {
                containsPii = true;
                Interlocked.Increment(ref _emailCount);
                string placeholder = $"[PII_EMAIL_{++emailCount}]";
                piiMap.Add(placeholder, match.Value);
                return placeholder;
            });

            // 4. 银行卡号脱敏
            anonymizedText = CreditCardRegex.Replace(anonymizedText, match =>
            {
                containsPii = true;
                Interlocked.Increment(ref _cardCount);
                string placeholder = $"[PII_CARD_{++cardCount}]";
                piiMap.Add(placeholder, match.Value);
                return placeholder;
            });

            // 5. IP 脱敏
            anonymizedText = IpRegex.Replace(anonymizedText, match =>
            {
                containsPii = true;
                Interlocked.Increment(ref _ipCount);
                string placeholder = $"[PII_IP_{++ipCount}]";
                piiMap.Add(placeholder, match.Value);
                return placeholder;
            });

            if (anonymizedText != originalText)
            {
                sanitizedMessages.Add(ChatMessage.FromText(msg.Role, anonymizedText));
            }
            else
            {
                sanitizedMessages.Add(msg);
            }
        }

        var sanitizedRequest = request with { Messages = sanitizedMessages };
        return (sanitizedRequest, piiMap, containsPii);
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
