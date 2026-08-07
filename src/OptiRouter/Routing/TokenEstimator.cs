using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// LLM token 估算器（纯静态，无 IO）。
/// </summary>
/// <remarks>
/// 经验系数：英文约 4 字符/token，中文约 1.5 字符/token（中文 token 密度高）。
/// 本实现按 rune 分桶后加权，比单一固定系数更贴近真实分布：
/// CJK 字符按 1.5 字符/token，ASCII 按 4 字符/token，其他（emoji/组合字符等）按 2.5 字符/token。
/// 每条消息另计固定开销（role 标记 + 分隔符，约 3 token）。
/// <para>
/// intentional-simple: 不引入真实 BPE tokenizer；分桶粗估对路由分级足够，中英文混合场景误差在 ~15% 内。
/// 如需精确，可升级为接入 tiktoken 系或模型官方 tokenizer。
/// </para>
/// </remarks>
public static class TokenEstimator
{
    private const double CharsPerTokenCjk = 1.5;
    private const double CharsPerTokenAscii = 4.0;
    private const double CharsPerTokenOther = 2.5;
    private const int TokensPerMessage = 3;

    /// <summary>
    /// 估算请求的输入 token 数。
    /// </summary>
    public static int Estimate(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return 0;

        int cjkChars = 0;
        int asciiChars = 0;
        int otherChars = 0;
        int messageCount = 0;

        foreach (var msg in request.Messages)
        {
            var text = msg.GetText();
            if (string.IsNullOrEmpty(text)) continue;
            messageCount++;

            foreach (var rune in text.EnumerateRunes())
            {
                int code = rune.Value;
                if (IsCjk(code)) cjkChars++;
                else if (code < 0x80) asciiChars++;
                else otherChars++;
            }
        }

        if (messageCount == 0) return 0;

        double contentTokens = cjkChars / CharsPerTokenCjk
            + asciiChars / CharsPerTokenAscii
            + otherChars / CharsPerTokenOther;

        return (int)Math.Ceiling(contentTokens) + messageCount * TokensPerMessage;
    }

    /// <summary>
    /// 判定码点是否属于 CJK 范围（中日韩统一表意文字及常用扩展区）。
    /// </summary>
    private static bool IsCjk(int code)
    {
        // CJK 统一表意文字（常用）+ 扩展 A + 兼容表意文字 + 日文假名 + 韩文谚文音节
        return (code >= 0x3000 && code <= 0x30FF)   // CJK 符号标点 + 假名
            || (code >= 0x3400 && code <= 0x4DBF)   // CJK 扩展 A
            || (code >= 0x4E00 && code <= 0x9FFF)   // CJK 常用
            || (code >= 0xAC00 && code <= 0xD7AF)   // 韩文音节
            || (code >= 0xF900 && code <= 0xFAFF)   // CJK 兼容表意
            || (code >= 0xFF00 && code <= 0xFFEF);  // 全角字符
    }
}
