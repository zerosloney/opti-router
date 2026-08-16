using OptiRouter.Compliance;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Compliance;

public class StreamingSlidingWindowFilterTests
{
    [Fact]
    public void ProcessChunk_NoSensitiveKeywords_ReturnsOriginalText()
    {
        var filter = new StreamingSlidingWindowFilter(new[] { "forbidden" });
        var buffer = new StreamingSlidingWindowBuffer(1024);

        var result = filter.ProcessChunk("Hello, this is a clean text stream.", buffer);

        Assert.False(result.IsViolation);
        Assert.Equal("Hello, this is a clean text stream.", result.ProcessedText);
        Assert.Null(result.MatchedKeyword);
    }

    [Fact]
    public void ProcessChunk_SingleChunkViolation_BlockMode_BlocksStream()
    {
        var filter = new StreamingSlidingWindowFilter(new[] { "malware" }, ComplianceAction.Block);
        var buffer = new StreamingSlidingWindowBuffer(1024);

        var result = filter.ProcessChunk("Here is a malware link for you.", buffer);

        Assert.True(result.IsViolation);
        Assert.Equal(string.Empty, result.ProcessedText);
        Assert.Equal("malware", result.MatchedKeyword);
    }

    [Fact]
    public void ProcessChunk_SplitKeywordAcrossChunks_BlockMode_InterceptsOnSecondChunk()
    {
        // Keyword "illegal_payload" split across Chunk 1 and Chunk 2
        var filter = new StreamingSlidingWindowFilter(new[] { "illegal_payload" }, ComplianceAction.Block);
        var buffer = new StreamingSlidingWindowBuffer(1024);

        // Chunk 1 contains "illegal_"
        var result1 = filter.ProcessChunk("Download the illegal_", buffer);
        Assert.False(result1.IsViolation);
        Assert.Equal("Download the illegal_", result1.ProcessedText);

        // Chunk 2 contains "payload file now."
        var result2 = filter.ProcessChunk("payload file now.", buffer);
        Assert.True(result2.IsViolation);
        Assert.Equal(string.Empty, result2.ProcessedText);
        Assert.Equal("illegal_payload", result2.MatchedKeyword);
    }

    [Fact]
    public void ProcessChunk_SingleChunkViolation_RedactMode_MasksKeyword()
    {
        var filter = new StreamingSlidingWindowFilter(new[] { "secret_key" }, ComplianceAction.Redact, "***");
        var buffer = new StreamingSlidingWindowBuffer(1024);

        var result = filter.ProcessChunk("Your key is secret_key inside system.", buffer);

        Assert.True(result.IsViolation);
        Assert.Equal("Your key is *** inside system.", result.ProcessedText);
        Assert.Equal("secret_key", result.MatchedKeyword);
    }

    [Fact]
    public void ProcessChunk_SplitKeywordAcrossChunks_RedactMode_MasksAcrossBuffer()
    {
        var filter = new StreamingSlidingWindowFilter(new[] { "forbidden_word" }, ComplianceAction.Redact, "[REDACTED]");
        var buffer = new StreamingSlidingWindowBuffer(1024);

        var result1 = filter.ProcessChunk("This text contains forbidden_", buffer);
        Assert.False(result1.IsViolation);

        var result2 = filter.ProcessChunk("word in output.", buffer);
        Assert.True(result2.IsViolation);
        Assert.Contains("[REDACTED]", result2.ProcessedText);
    }
}
