using OptiRouter.Clients;
using OptiRouter.Compression;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Compression;

public sealed class AdaptivePromptPrunerTests
{
    private readonly AdaptivePromptPruner _pruner = new(new BucketTokenEstimator());

    [Fact]
    public void Compress_WhenDisabled_ReturnsOriginalRequest()
    {
        var options = new PromptCompressionOptions { Enabled = false };
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Hello world!")
            }
        };

        var result = _pruner.Compress(request, options);

        Assert.False(result.WasCompressed);
        Assert.Same(request, result.CompressedRequest);
    }

    [Fact]
    public void Compress_BelowMinTokenThreshold_BypassesCompression()
    {
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 500
        };

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Quick short question.")
            }
        };

        var result = _pruner.Compress(request, options);

        Assert.False(result.WasCompressed);
        Assert.Contains("below_min_threshold", result.StrategySummary);
    }

    [Fact]
    public void Compress_DeduplicatesRepeatedSystemPrompts()
    {
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 10,
            DeduplicateSystemPrompts = true
        };

        var longSysPrompt = "You are an expert system architect and senior programmer with 20 years of experience in distributed systems and performance optimization.";
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", longSysPrompt),
                ChatMessage.FromText("user", "Question 1"),
                ChatMessage.FromText("assistant", "Answer 1"),
                ChatMessage.FromText("system", longSysPrompt), // Duplicate
                ChatMessage.FromText("user", "Question 2")
            }
        };

        var result = _pruner.Compress(request, options);

        var systemMessages = result.CompressedRequest.Messages.Where(m => m.Role == "system").ToList();
        Assert.Single(systemMessages);
        Assert.Equal(longSysPrompt, systemMessages[0].GetText());
    }

    [Fact]
    public void Compress_PrunesFillersInHistoricalTurns_PreservingRecentTurns()
    {
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 10,
            PreserveRecentTurns = 1, // Preserve only last 2 messages (1 turn)
            StripConversationalFillers = true
        };

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                // Old turn: contains filler
                ChatMessage.FromText("user", "Can you explain recursion in detail with all edge cases and examples?"),
                ChatMessage.FromText("assistant", "Sure, I would be happy to help with that! Recursion is a method where the solution depends on solutions to smaller instances. Hope this helps!"),
                // Recent turn (last 2 messages): preserved intact
                ChatMessage.FromText("user", "Give me a python example."),
                ChatMessage.FromText("assistant", "Sure, I can help with that! Here is python code.")
            }
        };

        var result = _pruner.Compress(request, options);

        // Old assistant message should have filler pruned
        var oldAssistantMsg = result.CompressedRequest.Messages[1].GetText();
        Assert.DoesNotContain("Sure, I would be happy to help with that!", oldAssistantMsg);
        Assert.DoesNotContain("Hope this helps!", oldAssistantMsg);
        Assert.Contains("Recursion is a method", oldAssistantMsg);

        // Recent assistant message should be preserved intact
        var recentAssistantMsg = result.CompressedRequest.Messages[3].GetText();
        Assert.Contains("Sure, I can help with that!", recentAssistantMsg);
    }

    [Fact]
    public void Compress_StrictlyPreservesCodeBlocksAndJson()
    {
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 10,
            PreserveRecentTurns = 0,
            PreserveCodeAndJson = true,
            StripConversationalFillers = true
        };

        string codeContent = "```csharp\npublic static void Main() {\n    // Certainly! Do not prune this comment\n    Console.WriteLine(\"Hello\");\n}\n```";

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("assistant", $"Sure, I can help with that! Here is the code:\n{codeContent}\nHope this helps!")
            }
        };

        var result = _pruner.Compress(request, options);
        var prunedText = result.CompressedRequest.Messages[0].GetText();

        Assert.Contains(codeContent, prunedText);
        Assert.DoesNotContain("Sure, I can help with that!", prunedText);
        Assert.DoesNotContain("Hope this helps!", prunedText);
    }

    [Fact]
    public void Compress_PreservesMultimodalHistoricalMessages()
    {
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 10,
            PreserveRecentTurns = 1, // 前 2 条进入历史剪枝分支
            StripConversationalFillers = true
        };

        var multimodalContent = System.Text.Json.JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "Analyze this architecture diagram" },
            new { type = "image_url", image_url = new { url = "https://example.com/diagram.png" } }
        });
        var multimodalMsg = new ChatMessage { Role = "user", Content = multimodalContent };

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Sure, I would be happy to help with that! Recursion is a method where the solution depends on solutions to smaller instances. Hope this helps!"),
                multimodalMsg, // 陈旧轮次的多模态消息：必须原样保留，不得重建为纯文本
                ChatMessage.FromText("assistant", "Answer"),
                ChatMessage.FromText("user", "Recent question")
            }
        };

        var result = _pruner.Compress(request, options);

        Assert.True(result.WasCompressed);
        var preserved = result.CompressedRequest.Messages.Single(m => ReferenceEquals(m, multimodalMsg));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, preserved.Content!.Value.ValueKind);
        Assert.Contains("image_url", preserved.Content!.Value.GetRawText());
    }

    [Fact]
    public void Compress_PrunedHistoryMessages_RetainToolCallExtensionData()
    {
        // 回归：历史消息被剪枝重建后，ExtensionData（tool_calls / tool_call_id /
        // reasoning_content）必须存活。曾用 FromText 重建导致扩展字段全部丢失，
        // 工具调用配对破裂，严格校验的上游（stepfun）返回 400
        // "invalid tool message, tool_call_id is required"。
        var options = new PromptCompressionOptions
        {
            Enabled = true,
            MinTokensToTrigger = 10,
            PreserveRecentTurns = 1, // 前 4 条（assistant+tool+user+assistant 中的前 2 条）进入历史剪枝
            StripConversationalFillers = true
        };

        // 文本含可剪枝的多余空白（连续空格 / 3+ 换行），确保走重建路径而非原样保留
        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = System.Text.Json.JsonSerializer.SerializeToElement("Reading   files now.\n\n\n\nDone."),
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["tool_calls"] = System.Text.Json.JsonSerializer.SerializeToElement(new object[]
                {
                    new { id = "call_1", type = "function", function = new { name = "read", arguments = "{\"path\":\"a.cs\"}" } }
                })
            }
        };
        var toolMsg = new ChatMessage
        {
            Role = "tool",
            Content = System.Text.Json.JsonSerializer.SerializeToElement("line1\n\n\n\nline2   end"),
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["tool_call_id"] = System.Text.Json.JsonSerializer.SerializeToElement("call_1")
            }
        };

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                assistantMsg, // 历史：触发剪枝重建
                toolMsg,      // 历史：触发剪枝重建
                ChatMessage.FromText("user", "Summarize"),
                ChatMessage.FromText("assistant", "Summary") // 最近轮次原样保留
            }
        };

        var result = _pruner.Compress(request, options);

        var prunedAssistant = result.CompressedRequest.Messages[0];
        var prunedTool = result.CompressedRequest.Messages[1];

        // 剪枝确实发生（重建路径被执行）：空白被规范化
        Assert.NotEqual(assistantMsg.GetText(), prunedAssistant.GetText());
        Assert.DoesNotContain("\n\n\n", prunedTool.GetText());

        // 扩展字段存活
        Assert.NotNull(prunedAssistant.ExtensionData);
        Assert.True(prunedAssistant.ExtensionData.ContainsKey("tool_calls"));
        Assert.Contains("call_1", prunedAssistant.ExtensionData["tool_calls"].GetRawText());

        Assert.NotNull(prunedTool.ExtensionData);
        Assert.True(prunedTool.ExtensionData.ContainsKey("tool_call_id"));
        Assert.Equal("call_1", prunedTool.ExtensionData["tool_call_id"].GetString());
    }
}
