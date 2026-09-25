using System.Text.RegularExpressions;

namespace OptiRouter.Endpoints;

/// <summary>
/// 审计内容兜底脱敏：请求内容摘要落库前的最后防线（不可还原掩码）。
/// <para>
/// <see cref="Routing.PiiAnonymizer"/> 是面向上游的可还原脱敏（占位符 + PiiMap）且默认关闭；
/// 本清洗器面向持久化——只要内容审计开着，用户文本里偶尔粘贴的密钥/令牌就不应原样进入
/// 审计库（审计库的读者面是整个管理台，且保留时长远长于单次请求）。
/// 只打高置信密钥形态（特征前缀/结构），不做通用 PII 识别，避免误伤正常问答内容。
/// </para>
/// </summary>
public static class AuditContentSanitizer
{
    private static readonly (Regex Pattern, string Replacement)[] Patterns =
    [
        // OpenAI 风格密钥（sk- 前缀，含各类兼容网关的自定义后缀）。
        (new Regex(@"sk-[A-Za-z0-9_-]{12,}", RegexOptions.Compiled), "sk-***"),
        // Authorization 头形态的 Bearer 令牌。
        (new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled), "Bearer ***"),
        // JWT 三段式（eyJ 开头的 base64url 头 + 两段）。
        (new Regex(@"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}", RegexOptions.Compiled), "***"),
        // GitHub PAT（ghp_/gho_/ghu_/ghs_/ghr_）。
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "***"),
        // AWS AccessKeyId / Slack / Google API key / 阿里云 AccessKey。
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "***"),
        (new Regex(@"xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled), "***"),
        (new Regex(@"AIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled), "***"),
        (new Regex(@"LTAI[0-9A-Za-z]{12,}", RegexOptions.Compiled), "***"),
        // 键值对形态：api_key=xxx / token: xxx（查询串、日志粘贴）与 JSON "api_key":"xxx"。
        (new Regex(@"(?i)\b(api[_-]?key|access[_-]?key|client[_-]?secret|token|secret|password|passwd|pwd)\s*[=:""]+\s*[""']?([A-Za-z0-9._~+/=-]{6,})", RegexOptions.Compiled), "$1=***"),
    ];

    /// <summary>对将持久化的请求内容执行密钥掩码。null/空原样返回；永不抛异常。</summary>
    public static string? Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        try
        {
            foreach (var (pattern, replacement) in Patterns)
            {
                content = pattern.Replace(content, replacement);
            }
            return content;
        }
        catch
        {
            // 脱敏是持久化前的卫生步骤：任何异常都以"不落原文"为保守侧处理。
            return null;
        }
    }
}
