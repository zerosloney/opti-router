using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP / Function Calling 工具调用参数自愈与清洗器 (Tool Call Arguments Auto-Sanitizer)。
/// 针对小模型或低成本开源模型在输出 Tool Call JSON 时极易出现的 Markdown 围栏、尾随逗号、单引号、
/// Python 关键字 (True/False/None) 及截断括号等常见畸形语法进行即时清洗与语法修复，
/// 确保下游 Agent 框架 (Claude Desktop, Cursor, LangChain, Roo Code 等) 永不因 JSON 解析崩溃。
/// </summary>
public sealed class McpToolCallSanitizer
{
    private static readonly Regex CodeBlockRegex = new(
        @"^```(?:json)?\s*([\s\S]*?)\s*```$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingCommaRegex = new(
        @",\s*([\]}])",
        RegexOptions.Compiled);

    private static readonly Regex PythonLiteralsRegex = new(
        @"\b(?<kw>True|False|None)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// 对完整的 OpenAI 兼容响应 JSON 字符串进行检查与清洗，自动修复 choices[].message.tool_calls[].function.arguments 中的非法 JSON。
    /// </summary>
    public string SanitizeResponseJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("\"tool_calls\"", StringComparison.OrdinalIgnoreCase))
        {
            return rawJson;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
            {
                return rawJson;
            }

            bool needsFix = false;
            foreach (var choice in choicesEl.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var msgEl)
                    && msgEl.TryGetProperty("tool_calls", out var toolCallsEl)
                    && toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsEl.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var funcEl)
                            && funcEl.TryGetProperty("arguments", out var argsEl)
                            && argsEl.ValueKind == JsonValueKind.String)
                        {
                            string? rawArgs = argsEl.GetString();
                            if (!IsValidJson(rawArgs ?? ""))
                            {
                                needsFix = true;
                                break;
                            }
                        }
                    }
                }
                if (needsFix) break;
            }

            if (!needsFix) return rawJson;

            // 使用 JsonNode 进行精确就地节点修复
            var node = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
            if (node?["choices"] is System.Text.Json.Nodes.JsonArray choicesArray)
            {
                foreach (var choiceNode in choicesArray)
                {
                    if (choiceNode?["message"]?["tool_calls"] is System.Text.Json.Nodes.JsonArray tcArray)
                    {
                        foreach (var tcNode in tcArray)
                        {
                            if (tcNode?["function"] is System.Text.Json.Nodes.JsonObject funcObj
                                && funcObj["arguments"] is System.Text.Json.Nodes.JsonValue argsVal)
                            {
                                string rawArgs = argsVal.GetValue<string>();
                                if (!IsValidJson(rawArgs))
                                {
                                    string fixedArgs = SanitizeJsonArguments(rawArgs);
                                    funcObj["arguments"] = fixedArgs;
                                }
                            }
                        }
                    }
                }
            }

            return node?.ToJsonString() ?? rawJson;
        }
        catch
        {
            return rawJson;
        }
    }

    /// <summary>
    /// 对工具调用的原始 arguments 字符串执行自愈清洗。
    /// 若输入本身已是合法 JSON，则原样快速返回；若存在畸形则依次执行规则修复。
    /// </summary>
    /// <param name="rawArguments">模型输出的裸 arguments 字符串。</param>
    /// <returns>修复后的合法 JSON 字符串。若完全无法修复则返回合法空对象 "{}"。</returns>
    public string SanitizeJsonArguments(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return "{}";
        }

        string trimmed = rawArguments.Trim();

        // 快速路径：若已是标准合法 JSON，直接返回
        if (IsValidJson(trimmed))
        {
            return trimmed;
        }

        // 1. 去除 Markdown 代码块包裹
        var match = CodeBlockRegex.Match(trimmed);
        if (match.Success)
        {
            trimmed = match.Groups[1].Value.Trim();
            if (IsValidJson(trimmed)) return trimmed;
        }

        // 2. 替换 Python 字面量 (True -> true, False -> false, None -> null)
        trimmed = PythonLiteralsRegex.Replace(trimmed, m => m.Value switch
        {
            "True" => "true",
            "False" => "false",
            "None" => "null",
            _ => m.Value
        });

        // 3. 消除尾随逗号 (Trailing comma)
        trimmed = TrailingCommaRegex.Replace(trimmed, "$1");
        if (IsValidJson(trimmed)) return trimmed;

        // 4. 单引号替换为双引号（简易状态机替换未转义的单引号）
        trimmed = FixSingleQuotes(trimmed);
        if (IsValidJson(trimmed)) return trimmed;

        // 5. 自动补齐未闭合的花括号或方括号（解决生成截断）
        trimmed = BalanceBrackets(trimmed);
        if (IsValidJson(trimmed)) return trimmed;

        // 若经所有尝试仍无法恢复，返回安全的空 JSON 对象，避免下游 Agent 解析直接崩毁
        return "{}";
    }

    /// <summary>
    /// 验证给定字符串是否为合法的 JSON。
    /// </summary>
    public static bool IsValidJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        try
        {
            using var doc = JsonDocument.Parse(input);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 修复未在双引号字符串内部的单引号为标准双引号。
    /// </summary>
    private static string FixSingleQuotes(string input)
    {
        if (!input.Contains('\'')) return input;

        var sb = new StringBuilder(input.Length);
        bool inDoubleQuotes = false;
        bool inSingleQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            char prev = i > 0 ? input[i - 1] : '\0';

            if (c == '"' && prev != '\\')
            {
                if (!inSingleQuotes)
                {
                    inDoubleQuotes = !inDoubleQuotes;
                }
                sb.Append(c);
            }
            else if (c == '\'' && prev != '\\')
            {
                if (!inDoubleQuotes)
                {
                    // 替换单引号为双引号
                    inSingleQuotes = !inSingleQuotes;
                    sb.Append('"');
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 自动补全因 max_tokens 截断而丢失的闭合括号/引号。
    /// </summary>
    private static string BalanceBrackets(string input)
    {
        var stack = new Stack<char>();
        bool inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            char prev = i > 0 ? input[i - 1] : '\0';

            if (c == '"' && prev != '\\')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes) continue;

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

        var sb = new StringBuilder(input);
        if (inQuotes)
        {
            sb.Append('"');
        }

        while (stack.Count > 0)
        {
            char open = stack.Pop();
            if (open == '{') sb.Append('}');
            else if (open == '[') sb.Append(']');
        }

        return sb.ToString();
    }
}
