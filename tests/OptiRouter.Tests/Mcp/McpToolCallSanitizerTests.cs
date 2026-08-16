using OptiRouter.Mcp;
using Xunit;

namespace OptiRouter.Tests.Mcp;

public class McpToolCallSanitizerTests
{
    private readonly McpToolCallSanitizer _sanitizer = new();

    [Fact]
    public void Sanitize_ValidJson_ReturnsUnchanged()
    {
        string json = "{\"city\":\"Beijing\",\"temperature\":25}";
        var result = _sanitizer.SanitizeJsonArguments(json);
        Assert.Equal(json, result);
    }

    [Fact]
    public void Sanitize_MarkdownCodeBlock_StripsFences()
    {
        string raw = "```json\n{\"location\": \"London\", \"days\": 5}\n```";
        var result = _sanitizer.SanitizeJsonArguments(raw);
        Assert.True(McpToolCallSanitizer.IsValidJson(result));
        Assert.Contains("\"location\"", result);
    }

    [Fact]
    public void Sanitize_PythonBooleansAndNone_ConvertsToJsonKeywords()
    {
        string raw = "{\"is_admin\": True, \"deleted\": False, \"extra\": None}";
        var result = _sanitizer.SanitizeJsonArguments(raw);
        Assert.True(McpToolCallSanitizer.IsValidJson(result));
        Assert.Contains("true", result);
        Assert.Contains("false", result);
        Assert.Contains("null", result);
    }

    [Fact]
    public void Sanitize_TrailingCommas_RemovesCommas()
    {
        string raw = "{\"query\": \"opti-router\", \"limit\": 10,}";
        var result = _sanitizer.SanitizeJsonArguments(raw);
        Assert.True(McpToolCallSanitizer.IsValidJson(result));
        Assert.DoesNotContain(",}", result);
    }

    [Fact]
    public void Sanitize_SingleQuotes_ConvertsToDoubleQuotes()
    {
        string raw = "{'name': 'OpenAI', 'type': 'gateway'}";
        var result = _sanitizer.SanitizeJsonArguments(raw);
        Assert.True(McpToolCallSanitizer.IsValidJson(result));
        Assert.Contains("\"name\"", result);
        Assert.Contains("\"OpenAI\"", result);
    }

    [Fact]
    public void Sanitize_TruncatedBrackets_AutoBalances()
    {
        string raw = "{\"filters\": {\"status\": \"active\"";
        var result = _sanitizer.SanitizeJsonArguments(raw);
        Assert.True(McpToolCallSanitizer.IsValidJson(result));
    }

    [Fact]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmptyObject()
    {
        Assert.Equal("{}", _sanitizer.SanitizeJsonArguments(null));
        Assert.Equal("{}", _sanitizer.SanitizeJsonArguments(""));
        Assert.Equal("{}", _sanitizer.SanitizeJsonArguments("   \n\t "));
    }

    [Fact]
    public void Sanitize_ResponseJson_RepairsMalformedToolCallArguments()
    {
        string rawResponse = @"{
            ""id"": ""chatcmpl-123"",
            ""choices"": [
                {
                    ""message"": {
                        ""role"": ""assistant"",
                        ""tool_calls"": [
                            {
                                ""id"": ""call_abc"",
                                ""type"": ""function"",
                                ""function"": {
                                    ""name"": ""search_database"",
                                    ""arguments"": ""```json\n{\""query\"": \""user_id\"", \""active\"": True,}\n```""
                                }
                            }
                        ]
                    }
                }
            ]
        }";

        string sanitized = _sanitizer.SanitizeResponseJson(rawResponse);
        using var doc = System.Text.Json.JsonDocument.Parse(sanitized);
        var argsStr = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString();

        Assert.NotNull(argsStr);
        Assert.True(McpToolCallSanitizer.IsValidJson(argsStr));
        Assert.Contains("\"active\": true", argsStr);
    }
}
