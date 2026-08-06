using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Tests;

public class OpenAICompatibleModelClientTests
{
    private static ModelEndpointOptions CreateEndpoint(string baseUrl = "https://api.openai.com", string apiKey = "sk-test", string name = "gpt-4o")
    {
        return new ModelEndpointOptions
        {
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = 30
        };
    }

    private static TestHandler CreateHandler(HttpResponseMessage response)
    {
        return new TestHandler(response);
    }

    private static IModelClient CreateClient(ModelEndpointOptions endpoint, TestHandler handler)
    {
        return ModelClientFactory.CreateForEndpoint(endpoint, handler);
    }

    #region CompleteAsync tests

    [Fact]
    public async Task CompleteAsync_WhenSuccess_ReturnsParsedChatResponse()
    {
        // Arrange
        var endpoint = CreateEndpoint(baseUrl: "https://api.openai.com/v1", name: "gpt-4o");
        var responseJson = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-123",
            model = "gpt-4o",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = "Hello!" },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
        });
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "ignored-model",
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "Hi" } },
            Stream = false
        };

        // Act
        var result = await client.CompleteAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("chatcmpl-123", result.Id);
        Assert.Equal("gpt-4o", result.Model);
        Assert.Single(result.Choices);
        Assert.Equal(0, result.Choices[0].Index);
        Assert.Equal("assistant", result.Choices[0].Message.Role);
        Assert.Equal("Hello!", result.Choices[0].Message.Content);
        Assert.Equal("stop", result.Choices[0].FinishReason);
        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage.PromptTokens);
        Assert.Equal(5, result.Usage.CompletionTokens);
        Assert.Equal(15, result.Usage.TotalTokens);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.GetLastRequestUri()?.AbsoluteUri);
    }

    [Fact]
    public async Task CompleteAsync_ForcesModelToEndpointName()
    {
        // Arrange
        var endpoint = CreateEndpoint(name: "forced-model");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "whatever",
            Messages = new List<ChatMessage>(),
            Stream = false
        };

        // Act
        await client.CompleteAsync(request);

        // Assert
        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        Assert.Equal("forced-model", doc.RootElement.GetProperty("model").GetString()!);
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_SerializesRequestInSnakeCase()
    {
        // Arrange
        var endpoint = CreateEndpoint(name: "gpt-4o");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "ignored",
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "Hi" } },
            MaxTokens = 100,
            Temperature = 0.7,
            Stream = false
        };

        // Act
        await client.CompleteAsync(request);

        // Assert
        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        // snake_case: max_tokens 存在，maxTokens 不存在
        Assert.True(doc.RootElement.TryGetProperty("max_tokens", out var maxTokens));
        Assert.Equal(100, maxTokens.GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("maxTokens", out _));
        Assert.True(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.True(doc.RootElement.TryGetProperty("model", out _));
        Assert.True(doc.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task CompleteAsync_WhenNon2xx_ThrowsModelClientException()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"server error\"}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>(),
            Stream = false
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ModelClientException>(async () => await client.CompleteAsync(request));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("server error", ex.Message);
    }

    #endregion

    #region StreamAsync tests

    [Fact]
    public async Task StreamAsync_WhenSuccess_YieldsCorrectChunks()
    {
        // Arrange
        var endpoint = CreateEndpoint(baseUrl: "https://api.openai.com/v1");
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n");
        sse.Append("data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"},\"finish_reason\":null}]}\n\n");
        sse.Append("data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n");
        sse.Append("data: [DONE]\n\n");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>(),
            Stream = true
        };

        // Act
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in client.StreamAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hello", chunks[0].DeltaContent);
        Assert.Null(chunks[0].FinishReason);
        Assert.Equal(" world", chunks[1].DeltaContent);
        Assert.Null(chunks[1].FinishReason);
        Assert.Equal("stop", chunks[2].FinishReason);
        Assert.Equal(5, chunks[2].Usage!.PromptTokens);
        Assert.Equal(2, chunks[2].Usage!.CompletionTokens);
        Assert.Equal(7, chunks[2].Usage!.TotalTokens);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.GetLastRequestUri()?.AbsoluteUri);
    }

    [Fact]
    public async Task StreamAsync_WhenDone_EndsGracefully()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var sse = "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>(),
            Stream = true
        };

        // Act
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in client.StreamAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.Single(chunks);
        Assert.Equal("stop", chunks[0].FinishReason);
    }

    [Fact]
    public async Task StreamAsync_WhenNon2xx_ThrowsModelClientException()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad\"}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>(),
            Stream = true
        };

        // Act & Assert
        await Assert.ThrowsAsync<ModelClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(request))
            {
                // 不应执行到此处。
            }
        });
    }

    #endregion

    #region ProbeAsync tests

    [Fact]
    public async Task ProbeAsync_WhenSuccess_ReturnsHealthy()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "chatcmpl-p",
                model = "gpt-4o",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "pong" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
            }), Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        // Act
        var result = await client.ProbeAsync();

        // Assert
        Assert.True(result.Healthy);
        Assert.True(result.LatencyMs >= 0);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_WhenFailure_ReturnsUnhealthy()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        // Act
        var result = await client.ProbeAsync();

        // Assert
        Assert.False(result.Healthy);
        Assert.True(result.LatencyMs >= 0);
        Assert.NotNull(result.Error);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// 可记录请求内容的 HttpMessageHandler，用于测试。
    /// </summary>
    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private HttpRequestMessage? _lastRequest;
        private string? _lastContent;

        public TestHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _lastRequest = request;
            if (request.Content != null)
            {
                _lastContent = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                _lastContent = null;
            }

            return Task.FromResult(_response);
        }

        public string? GetLastRequestContent()
        {
            return _lastContent;
        }

        public Uri? GetLastRequestUri()
        {
            return _lastRequest?.RequestUri;
        }
    }

    #endregion
}
