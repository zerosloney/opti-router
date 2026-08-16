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
}
