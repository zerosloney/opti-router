using OptiRouter.Clients;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// RequestContent 审计存储开关测试。
/// </summary>
public sealed class RequestContentAuditTests
{
    [Fact]
    public void ExtractRequestContentSummary_WithUserMessage_ReturnsContent()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("system", "You are a helpful assistant."),
                ChatMessage.FromText("user", "What is the capital of France?")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Equal("What is the capital of France?", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithLongUserMessage_TruncatesTo500Chars()
    {
        // Arrange
        var longText = new string('A', 600);
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", longText)
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.NotNull(summary);
        Assert.True(summary!.Length <= 503); // 500 chars + "..."
        Assert.EndsWith("...", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithMultipleUserMessages_ReturnsLastUserMessage()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", "First message"),
                ChatMessage.FromText("assistant", "Response to first"),
                ChatMessage.FromText("user", "Second message (should be returned)")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Equal("Second message (should be returned)", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithOnlyAssistantMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("assistant", "Hello!")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithEmptyMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = Array.Empty<ChatMessage>()
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithNullMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = null!
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithEmptyUserMessage_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", ""),
                ChatMessage.FromText("user", "   ")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }
}
