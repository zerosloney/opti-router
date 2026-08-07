using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ResponseConfidenceCheckerTests
{
    [Fact]
    public void ExtractAssistantText_ParsesStringContent()
    {
        var body = """{"choices":[{"message":{"role":"assistant","content":"The answer is 42."}}]}""";
        var response = new RawChatResponse(body, null);

        var text = ResponseConfidenceChecker.ExtractAssistantText(response);

        Assert.Equal("The answer is 42.", text);
    }

    [Fact]
    public void ExtractAssistantText_ParsesMultimodalArrayContent()
    {
        var body = """{"choices":[{"message":{"content":[{"type":"text","text":"part1 "},{"type":"text","text":"part2"}]}}]}""";
        var response = new RawChatResponse(body, null);

        var text = ResponseConfidenceChecker.ExtractAssistantText(response);

        Assert.Equal("part1 part2", text);
    }

    [Fact]
    public void ExtractAssistantText_MalformedJson_ReturnsEmpty()
    {
        var response = new RawChatResponse("not valid json {{{", null);

        var text = ResponseConfidenceChecker.ExtractAssistantText(response);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ExtractAssistantText_NoChoices_ReturnsEmpty()
    {
        var body = """{"choices":[]}""";
        var response = new RawChatResponse(body, null);

        var text = ResponseConfidenceChecker.ExtractAssistantText(response);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void ExtractAssistantText_EmptyBody_ReturnsEmpty()
    {
        var response = new RawChatResponse("", null);

        var text = ResponseConfidenceChecker.ExtractAssistantText(response);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void IsConfident_RecognizesConfidentToken()
    {
        var response = new ChatResponse
        {
            Choices = new List<ChatChoice> { new() { Message = ChatMessage.FromText("assistant", "CONFIDENT") } }
        };

        Assert.True(ResponseConfidenceChecker.IsConfident(response));
    }

    [Fact]
    public void IsConfident_RecognizesUncertainToken()
    {
        var response = new ChatResponse
        {
            Choices = new List<ChatChoice> { new() { Message = ChatMessage.FromText("assistant", "I am UNCERTAIN about this") } }
        };

        Assert.False(ResponseConfidenceChecker.IsConfident(response));
    }

    [Fact]
    public void IsConfident_CaseInsensitive()
    {
        var response = new ChatResponse
        {
            Choices = new List<ChatChoice> { new() { Message = ChatMessage.FromText("assistant", "confident") } }
        };

        Assert.True(ResponseConfidenceChecker.IsConfident(response));
    }

    [Fact]
    public void IsConfident_NeitherToken_TreatedAsUncertain()
    {
        // 容错优先升级：模型回非预期文本（如"yes"/解释）→ 视为不自信触发升级，宁升勿漏。
        var response = new ChatResponse
        {
            Choices = new List<ChatChoice> { new() { Message = ChatMessage.FromText("assistant", "looks correct to me") } }
        };

        Assert.False(ResponseConfidenceChecker.IsConfident(response));
    }

    [Fact]
    public void IsConfident_EmptyResponse_ReturnsFalse()
    {
        var response = new ChatResponse { Choices = new List<ChatChoice>() };

        Assert.False(ResponseConfidenceChecker.IsConfident(response));
    }

    [Fact]
    public void BuildVerificationRequest_HasThreeMessagesInOrder()
    {
        var original = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "What is 2+2?") }
        };

        var verifyReq = ResponseConfidenceChecker.BuildVerificationRequest(original, "4", "verify prompt");

        Assert.Equal(3, verifyReq.Messages.Count);
        Assert.Equal("user", verifyReq.Messages[0].Role);
        Assert.Equal("What is 2+2?", verifyReq.Messages[0].GetText());
        Assert.Equal("assistant", verifyReq.Messages[1].Role);
        Assert.Equal("4", verifyReq.Messages[1].GetText());
        Assert.Equal("user", verifyReq.Messages[2].Role);
        Assert.Equal("verify prompt", verifyReq.Messages[2].GetText());
    }

    [Fact]
    public void BuildVerificationRequest_ForcesZeroTemperature()
    {
        var original = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "q") }
        };

        var verifyReq = ResponseConfidenceChecker.BuildVerificationRequest(original, "a", "p");

        Assert.Equal(0, verifyReq.Temperature);
    }

    [Fact]
    public void BuildVerificationRequest_UsesLastUserMessageAsQuestion()
    {
        var original = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "be helpful"),
                ChatMessage.FromText("user", "first question"),
                ChatMessage.FromText("assistant", "first answer"),
                ChatMessage.FromText("user", "second question")
            }
        };

        var verifyReq = ResponseConfidenceChecker.BuildVerificationRequest(original, "cheap answer", "p");

        Assert.Equal("second question", verifyReq.Messages[0].GetText());
    }
}
