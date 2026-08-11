using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiRouter.Routing;

/// <summary>
/// JSON AST 自动化修补工具：
/// 解决大模型输出 JSON 时常见的 Markdown 围栏包裹、开场/结尾解释语污染、
/// 控制字符、尾部多余逗号（Trailing Comma）及截断导致的丢失闭合括号问题。
/// </summary>
public static class JsonAstRepairer
{
    private static readonly Regex CodeFenceRegex = new(
        @"```(?:json)?\s*(.*?)\s*```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingCommaRegex = new(
        @",\s*([}\]])",
        RegexOptions.Compiled);

    /// <summary>
    /// 自动清理并修补大模型返回的 JSON 文本。
    /// </summary>
    /// <param name="rawText">包含 JSON 的原始文本。</param>
    /// <returns>修复后的纯 JSON 字符串。</returns>
    public static string RepairJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        string text = rawText.Trim();

        // 1. 剥离 Markdown ```json ... ``` 代码围栏
        var fenceMatch = CodeFenceRegex.Match(text);
        if (fenceMatch.Success)
        {
            text = fenceMatch.Groups[1].Value.Trim();
        }
        else if (text.StartsWith("```"))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
                text = text[(firstNewline + 1)..];
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                text = text[..lastFence];
            text = text.Trim();
        }

        // 2. 剥离前置/后置说明性闲聊文本（提取首个 '{' 或 '[' 到最后一个 '}' 或 ']' 的范围）
        int firstBracket = text.IndexOfAny(['{', '[']);
        int lastBracket = text.LastIndexOfAny(['}', ']']);

        if (firstBracket >= 0)
        {
            if (lastBracket > firstBracket)
            {
                text = text.Substring(firstBracket, lastBracket - firstBracket + 1);
            }
            else
            {
                // 可能由于 MaxTokens 被阶段性截断，缺失了闭合括号
                text = text[firstBracket..];
            }
        }

        // 3. 修复尾部非法多余逗号 [1, 2,] -> [1, 2] 或 {"a": 1,} -> {"a": 1}
        text = TrailingCommaRegex.Replace(text, "$1");

        // 4. 清洗不可见控制字符（除了正常换行/制表符/回车）
        text = CleanControlCharacters(text);

        // 5. 补全因截断导致的缺失闭合括号
        text = AutoCloseBrackets(text);

        return text.Trim();
    }

    /// <summary>
    /// 尝试解析修复后的 JSON，并判断是否合法。
    /// </summary>
    public static bool TryParse(string rawText, out JsonDocument? doc)
    {
        doc = null;
        string repaired = RepairJson(rawText);
        if (string.IsNullOrWhiteSpace(repaired))
            return false;

        try
        {
            doc = JsonDocument.Parse(repaired);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CleanControlCharacters(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string AutoCloseBrackets(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var stack = new Stack<char>();
        bool inString = false;
        bool isEscaped = false;

        foreach (char c in text)
        {
            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (c == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c is '{' or '[')
            {
                stack.Push(c);
            }
            else if (c is '}' or ']')
            {
                if (stack.Count > 0)
                {
                    char top = stack.Peek();
                    if ((c == '}' && top == '{') || (c == ']' && top == '['))
                    {
                        stack.Pop();
                    }
                }
            }
        }

        // 如果在字符串内部中断，补齐关闭双引号
        var sb = new StringBuilder(text);
        if (inString)
        {
            sb.Append('"');
        }

        // 补全缺失的闭合括号/大括号
        while (stack.Count > 0)
        {
            char top = stack.Pop();
            sb.Append(top == '{' ? '}' : ']');
        }

        return sb.ToString();
    }
}
