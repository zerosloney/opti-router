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
    private const int NonStreamingResponseLimitBytes = 1024 * 1024;

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

    private static IModelClient CreateClient(ModelEndpointOptions endpoint, HttpMessageHandler handler)
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
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
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
        Assert.Equal("Hello!", result.Choices[0].Message.Content!.Value.GetString());
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
    public async Task CompleteAsync_SendsUpstreamModelId_WhenIdConfigured()
    {
        // Name 是路由名，Id 是发往上游的真实模型；两者分离时上游应收到 Id。
        var endpoint = CreateEndpoint(name: "deepseek/deepseek-chat");
        endpoint.Id = "deepseek-chat";
        Assert.Equal("deepseek-chat", endpoint.UpstreamModelId);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        await client.CompleteAsync(new ChatRequest { Model = "deepseek/deepseek-chat", Messages = new List<ChatMessage>() });

        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        Assert.Equal("deepseek-chat", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsNameAsModel_WhenIdAbsent()
    {
        // 未配置 Id 时回退 Name，保持既有行为。
        var endpoint = CreateEndpoint(name: "gpt-4o");
        Assert.Equal("gpt-4o", endpoint.UpstreamModelId);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        await client.CompleteAsync(new ChatRequest { Model = "whatever", Messages = new List<ChatMessage>() });

        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        Assert.Equal("gpt-4o", doc.RootElement.GetProperty("model").GetString());
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
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
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
        Assert.Contains("server error", ex.ResponseBody);
        Assert.DoesNotContain("server error", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_OversizedContentLengthSuccess_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateOversizedResponseContent(knownLength: true)
        };
        Assert.Equal(NonStreamingResponseLimitBytes + 1, response.Content.Headers.ContentLength);
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(
            () => client.CompleteAsync(new ChatRequest()));

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
    }

    [Fact]
    public async Task CompleteAsync_OversizedChunkedSuccess_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateOversizedResponseContent(knownLength: false)
        };
        Assert.Null(response.Content.Headers.ContentLength);
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(
            () => client.CompleteAsync(new ChatRequest()));

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
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

    /// <summary>构造探活流式 SSE 响应（探活自 2026-08 起走流式：部分网关非流式补全会挂死）。</summary>
    private static HttpResponseMessage SseResponse(params string[] dataLines)
    {
        var sse = new StringBuilder();
        foreach (var line in dataLines)
            sse.Append($"data: {line}\n\n");
        sse.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    [Fact]
    public async Task ProbeAsync_DoesNotCapMaxTokens_ReasoningModelsRejectTinyBudget()
    {
        // reasoning 模型（如 ox-alpha）思考会耗尽小额度导致内容为空，上游直接
        // 500 "empty response content"（实测 max_tokens 1~32 均 500）。
        // 探活不设上限（null 不序列化），由上游取默认值；探活走流式（stream:true）。
        var endpoint = CreateEndpoint();
        var response = SseResponse("{\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}");
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        await client.ProbeAsync();

        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.True(doc.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean());
    }

    [Fact]
    public async Task ProbeAsync_ExtractsIdentityReply_FromStreamedDeltas()
    {
        var endpoint = CreateEndpoint();
        var response = SseResponse(
            "{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "{\"choices\":[{\"delta\":{\"content\":\"我是 DeepSeek\"}}]}");
        var client = CreateClient(endpoint, CreateHandler(response));

        var result = await client.ProbeAsync();

        Assert.True(result.Healthy);
        Assert.Equal("我是 DeepSeek", result.Reply);
    }

    [Fact]
    public async Task ProbeAsync_SkipsSseCommentLines_OpenRouterKeepalive()
    {
        // 聚合网关（commandcode.ai → OpenRouter）流式首部刷 ": OPENROUTER PROCESSING" 注释行；
        // SSE 注释不是 data 行，探活必须忽略而非误判失败。
        var endpoint = CreateEndpoint();
        var sse = ": OPENROUTER PROCESSING\n\n" +
                  ": OPENROUTER PROCESSING\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"我是 GLM\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        var client = CreateClient(endpoint, CreateHandler(response));

        var result = await client.ProbeAsync();

        Assert.True(result.Healthy);
        Assert.Equal("我是 GLM", result.Reply);
    }

    [Fact]
    public async Task ProbeAsync_WhenSuccess_ReturnsHealthy()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        var handler = CreateHandler(SseResponse("{\"choices\":[{\"delta\":{\"content\":\"pong\"}}]}"));
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

    [Fact]
    public async Task ProbeAsync_WhenUpstreamStalls_TimesOut()
    {
        // 上游挂住（不回响应头，非流式挂死网关的真实形态）：探活必须在窗口内放弃并返回
        // "Probe timed out."，而非无限等待。Handler 层模拟挂起——SendAsync 永不完成、随令牌取消。
        var endpoint = CreateEndpoint();
        var client = CreateClient(endpoint, new StallingHandler());

        var result = await client.ProbeAsync(timeout: TimeSpan.FromMilliseconds(400));

        Assert.False(result.Healthy);
        Assert.Equal("Probe timed out.", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_WhenInBandError_ReturnsUnhealthyWithUpstreamMessage()
    {
        // Zen 排队 79s 后吐流内 error 事件的形态：探活必须判不健康且带上游原话，
        // 而非把 error 行当内容中继后记健康。
        var endpoint = CreateEndpoint();
        var handler = CreateHandler(SseResponse(
            "{\"error\":{\"message\":\"An internal error occurred. Retry once; if it persists, contact support with your request_id.\",\"code\":524}}"));
        var client = CreateClient(endpoint, handler);

        var result = await client.ProbeAsync();

        Assert.False(result.Healthy);
        Assert.Contains("An internal error occurred", result.Error);
    }

    [Fact]
    public async Task StreamRawAsync_WhenInBandErrorEvent_ThrowsWithUpstreamMessage()
    {
        // 流内 error 事件必须抛 ModelClientException（失败信号 → 编排器 failover/审计/熔断接管），
        // 而非原样中继成内容行导致审计假成功。
        var endpoint = CreateEndpoint();
        var handler = CreateHandler(SseResponse(
            "{\"error\":{\"message\":\"An internal error occurred.\",\"code\":524}}"));
        var client = CreateClient(endpoint, handler);

        var ex = await Assert.ThrowsAsync<ModelClientException>(async () =>
        {
            await foreach (var _ in client.StreamRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>(), Stream = true }))
            {
            }
        });

        Assert.Contains("An internal error occurred.", ex.Message);
        Assert.Equal(524, (int)ex.StatusCode);
    }

    [Fact]
    public async Task CompleteRawAsync_When200BodyCarriesError_ThrowsWithUpstreamMessage()
    {
        // 非流式 200 + body 带 error 字段（OpenRouter 系聚合网关形态）：同样视为上游失败。
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"error":{"message":"upstream unavailable","code":502}}""", Encoding.UTF8, "application/json")
        };
        var client = CreateClient(endpoint, CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ModelClientException>(async () =>
            await client.CompleteRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>() }));

        Assert.Contains("upstream unavailable", ex.Message);
        Assert.Equal(502, (int)ex.StatusCode);
    }

    [Fact]
    public async Task StreamRawAsync_WhenErrorFieldNull_NormalContentPasses()
    {
        // 防误伤："error": null 是合法内容块字段，不得触发失败路径。
        var endpoint = CreateEndpoint();
        var handler = CreateHandler(SseResponse(
            "{\"choices\":[{\"delta\":{\"content\":\"hi\"}}],\"error\":null}"));
        var client = CreateClient(endpoint, handler);

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>(), Stream = true }))
        {
            lines.Add(line);
        }

        Assert.Equal(2, lines.Count); // 内容行 + [DONE]
        Assert.Contains("\"content\":\"hi\"", lines[0].Data);
    }

    [Fact]
    public async Task StreamRawAsync_WhenUpstreamClosesWithoutDone_ThrowsBadGateway()
    {
        // 上游连接关闭但从未发 [DONE]（协议违约断流）：必须抛异常走失败路径（failover/审计/熔断），
        // 此前静默结束被当成功——客户端收断头流报 "Stream ended without finish_reason" 而审计 OK。
        var endpoint = CreateEndpoint();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        var client = CreateClient(endpoint, CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ModelClientException>(async () =>
        {
            await foreach (var _ in client.StreamRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>(), Stream = true }))
            {
            }
        });

        Assert.Equal(502, (int)ex.StatusCode);
        Assert.Contains("without [DONE]", ex.ResponseBody);
    }

    [Fact]
    public async Task StreamRawAsync_TailDoneWithoutTrailingNewline_YieldsDone()
    {
        // 防误伤：部分上游最后一个 [DONE] 不带尾换行直接关流，残行处理须识别为正常终止。
        var endpoint = CreateEndpoint();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        var client = CreateClient(endpoint, CreateHandler(response));

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>(), Stream = true }))
        {
            lines.Add(line);
        }

        Assert.Equal(2, lines.Count); // 内容行 + [DONE]
        Assert.Equal("[DONE]", lines[1].Data);
    }

    [Fact]
    public async Task StreamRawAsync_TailDataLineWithoutNewline_RelaysThenThrows()
    {
        // 残行是普通 data 行（无 [DONE] 结尾）：半行先转发，随后仍判断流抛 502。
        var endpoint = CreateEndpoint();
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"b\"";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
        var client = CreateClient(endpoint, CreateHandler(response));

        var lines = new List<RawStreamLine>();
        var ex = await Assert.ThrowsAsync<ModelClientException>(async () =>
        {
            await foreach (var line in client.StreamRawAsync(new ChatRequest { Model = "m", Messages = new List<ChatMessage>(), Stream = true }))
            {
                lines.Add(line);
            }
        });

        Assert.Equal(502, (int)ex.StatusCode);
        Assert.Equal(2, lines.Count); // 完整内容行 + 转发的残行
        Assert.Contains("\"content\":\"b\"", lines[1].Data);
    }

    [Fact]
    public void ExtractInBandError_NormalizesOutOfRangeCode()
    {
        // 业务码（如 MiniMax 1000）越出 [400,599] → 回退 500，避免伪造怪异 HTTP 状态。
        var ex = OpenAICompatibleModelClient.ExtractInBandError(
            """{"error":{"message":"biz fail","code":1000}}""", System.Net.HttpStatusCode.OK, null);
        Assert.NotNull(ex);
        Assert.Equal(500, (int)ex!.StatusCode);
        Assert.Contains("biz fail", ex.Message);

        // error 为字符串形态（部分网关）也能提取。
        var ex2 = OpenAICompatibleModelClient.ExtractInBandError(
            """{"error":"boom"}""", System.Net.HttpStatusCode.OK, null);
        Assert.NotNull(ex2);
        Assert.Equal("boom", ex2!.Message);

        // 正常内容不误判。
        Assert.Null(OpenAICompatibleModelClient.ExtractInBandError(
            """{"choices":[{"delta":{"content":"ok"}}]}""", System.Net.HttpStatusCode.OK, null));
        Assert.Null(OpenAICompatibleModelClient.ExtractInBandError("[DONE]", System.Net.HttpStatusCode.OK, null));
        Assert.Null(OpenAICompatibleModelClient.ExtractInBandError(null, System.Net.HttpStatusCode.OK, null));
    }

    /// <summary>SendAsync 永不完成、随取消令牌立即结束的 Handler：模拟挂死上游。</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
            {
                await tcs.Task.ConfigureAwait(false);
            }
            throw new System.Diagnostics.UnreachableException();
        }
    }

    #endregion

    #region CompleteRawAsync / StreamRawAsync tests

    [Fact]
    public async Task CompleteRawAsync_WhenSuccess_ReturnsOriginalBodyAndExtractsUsage()
    {
        // Arrange: 含上游自定义字段 logprobs，透传后应原样保留
        var endpoint = CreateEndpoint(baseUrl: "https://api.openai.com/v1", name: "gpt-4o");
        var responseJson = "{\"id\":\"chatcmpl-x\",\"model\":\"gpt-4o\",\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":8,\"total_tokens\":20},\"logprobs\":{\"tokens\":[\"a\"]}}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var request = new ChatRequest
        {
            Model = "ignored",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
            Stream = false
        };

        // Act
        var result = await client.CompleteRawAsync(request);

        // Assert: 原始 body 原样返回（含 logprobs 自定义字段）
        Assert.Equal(responseJson, result.Body);
        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage!.PromptTokens);
        Assert.Equal(8, result.Usage!.CompletionTokens);
        Assert.Equal(20, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task CompleteRawAsync_ForcesModelToEndpointName()
    {
        var endpoint = CreateEndpoint(name: "forced-model");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        await client.CompleteRawAsync(new ChatRequest { Model = "whatever", Messages = new List<ChatMessage>() });

        var sentBody = handler.GetLastRequestContent();
        Assert.NotNull(sentBody);
        using var doc = JsonDocument.Parse(sentBody);
        Assert.Equal("forced-model", doc.RootElement.GetProperty("model").GetString()!);
    }

    [Fact]
    public async Task CompleteRawAsync_WhenNoUsage_ReturnsNullUsage()
    {
        var endpoint = CreateEndpoint();
        // 上游未返回 usage 字段
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"x\",\"model\":\"gpt-4o\",\"choices\":[]}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var result = await client.CompleteRawAsync(new ChatRequest { Model = "gpt-4o", Messages = new List<ChatMessage>() });

        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task CompleteRawAsync_WhenNon2xx_ThrowsModelClientException()
    {
        var endpoint = CreateEndpoint();
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var ex = await Assert.ThrowsAsync<ModelClientException>(
            async () => await client.CompleteRawAsync(new ChatRequest { Model = "gpt-4o", Messages = new List<ChatMessage>() }));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task CompleteRawAsync_OversizedContentLengthError_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = CreateOversizedResponseContent(knownLength: true)
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(
            () => client.CompleteRawAsync(new ChatRequest()));

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
    }

    [Fact]
    public async Task CompleteRawAsync_OversizedChunkedError_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = CreateOversizedResponseContent(knownLength: false)
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(
            () => client.CompleteRawAsync(new ChatRequest()));

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
    }

    [Fact]
    public async Task StreamAsync_OversizedContentLengthError_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = CreateOversizedResponseContent(knownLength: true)
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new ChatRequest { Stream = true }))
            {
            }
        });

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
    }

    [Fact]
    public async Task StreamRawAsync_OversizedChunkedError_ThrowsResponseSizeLimitExceededException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = CreateOversizedResponseContent(knownLength: false)
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var ex = await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(async () =>
        {
            await foreach (var _ in client.StreamRawAsync(new ChatRequest { Stream = true }))
            {
            }
        });

        Assert.Equal(NonStreamingResponseLimitBytes, ex.LimitBytes);
    }

    [Fact]
    public async Task StreamRawAsync_WhenSuccess_YieldsOriginalDataLines()
    {
        var endpoint = CreateEndpoint(baseUrl: "https://api.openai.com/v1");
        // 上游 SSE 含自定义字段，透传应原样保留
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"logprobs\":{\"x\":1}}]}\n\n");
        sse.Append("data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1,\"total_tokens\":4}}\n\n");
        sse.Append("data: [DONE]\n\n");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(new ChatRequest { Model = "gpt-4o", Messages = new List<ChatMessage>(), Stream = true }))
        {
            lines.Add(line);
        }

        // 两个 data 行 + [DONE]
        Assert.Equal(3, lines.Count);
        // 第一行原样保留含 logprobs
        Assert.Contains("\"logprobs\":{\"x\":1}", lines[0].Data);
        Assert.Null(lines[0].Usage);
        // 第二行提取 usage
        Assert.NotNull(lines[1].Usage);
        Assert.Equal(4, lines[1].Usage!.TotalTokens);
        // DONE 标记原样
        Assert.Equal("[DONE]", lines[2].Data);
        Assert.Null(lines[2].Usage);
    }

    [Fact]
    public async Task StreamRawAsync_OversizedSingleLine_AbortsToPreventOom()
    {
        var endpoint = CreateEndpoint(baseUrl: "https://api.openai.com/v1");
        // 构造一条无换行、超过 MaxStreamLineBytes(1MB) 的 data 行，模拟恶意上游。
        // 用 'a' 填充至 2MB，无 \n 触发行累计检查。
        var oversizedLine = "data: " + new string('a', 2 * 1024 * 1024);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversizedLine, Encoding.UTF8, "text/event-stream")
        };
        var handler = CreateHandler(response);
        var client = CreateClient(endpoint, handler);

        // 超限应抛 ResponseSizeLimitExceededException（专用异常，供 endpoint 精确分类为 RESPONSE_TOO_LARGE），
        // 而非把整行读入后 yield。
        await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(async () =>
        {
            await foreach (var _ in client.StreamRawAsync(new ChatRequest { Model = "gpt-4o", Messages = new List<ChatMessage>(), Stream = true }))
            {
                // 不应到达此处
            }
        });
    }

    [Fact]
    public async Task CompleteRawAsync_NormalizesOpenAiCacheUsageAndKnownHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"usage":{"prompt_tokens":100,"completion_tokens":5,"total_tokens":105,
                "prompt_tokens_details":{"cached_tokens":60,"cache_write_tokens":10}}}
                """, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "7");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", "900");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "2s");
        response.Headers.TryAddWithoutValidation("x-unknown-secret-header", "must-not-be-retained");
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var result = await client.CompleteRawAsync(new ChatRequest());

        Assert.NotNull(result.Usage);
        Assert.Equal(60, result.Usage!.CachedInputTokens);
        Assert.Equal(10, result.Usage.CacheWriteInputTokens);
        Assert.Equal(30, result.Usage.UncachedInputTokens);
        Assert.Equal(7, result.Metadata!.RequestsRemaining);
        Assert.Equal(900, result.Metadata.TokensRemaining);
        Assert.NotNull(result.Metadata.RequestsResetAt);
        Assert.NotNull(result.Metadata.ResponseHeaderLatencyMs);
    }

    [Fact]
    public async Task CompleteRawAsync_NormalizesDeepSeekHitMissAndIgnoresMalformedOptionalUsage()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"usage":{"prompt_tokens":80,"completion_tokens":4,
                "prompt_cache_hit_tokens":50,"prompt_cache_miss_tokens":30,
                "cache_creation_input_tokens":"bad"}}
                """, Encoding.UTF8, "application/json")
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var result = await client.CompleteRawAsync(new ChatRequest());

        Assert.Equal(50, result.Usage!.CachedInputTokens);
        Assert.Equal(30, result.Usage.UncachedInputTokens);
        Assert.Equal(0, result.Usage.CacheWriteInputTokens);
        Assert.Null(result.Metadata!.RequestsRemaining);
        Assert.Null(result.Metadata.TokensRemaining);
    }

    [Fact]
    public async Task CompleteRawAsync_InconsistentCacheCountsUseSafeUncachedRemainder()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"usage":{"prompt_tokens":100,"completion_tokens":4,
                "prompt_cache_hit_tokens":20,"prompt_cache_miss_tokens":30}}
                """, Encoding.UTF8, "application/json")
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var result = await client.CompleteRawAsync(new ChatRequest());

        Assert.Equal(20, result.Usage!.CachedInputTokens);
        Assert.Equal(80, result.Usage.UncachedInputTokens);
        Assert.Equal(result.Usage.PromptTokens,
            result.Usage.CachedInputTokens + result.Usage.CacheWriteInputTokens + result.Usage.UncachedInputTokens);
    }

    [Fact]
    public async Task CompleteRawAsync_MalformedUsageShapeDoesNotFailResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"ok\",\"usage\":\"malformed\"}", Encoding.UTF8, "application/json")
        };
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));

        var result = await client.CompleteRawAsync(new ChatRequest());

        Assert.Null(result.Usage);
        Assert.Contains("\"id\":\"ok\"", result.Body);
    }

    [Fact]
    public async Task StreamRawAsync_AttachesTtftMetadataOnlyToFirstDataItem()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"id\":\"a\",\"choices\":[]}\n\n" +
                "data: {\"id\":\"b\",\"choices\":[]}\n\n" +
                "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        };
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "4");
        var client = CreateClient(CreateEndpoint(), CreateHandler(response));
        var lines = new List<RawStreamLine>();

        await foreach (var line in client.StreamRawAsync(new ChatRequest { Stream = true }))
            lines.Add(line);

        Assert.NotNull(lines[0].Metadata?.TimeToFirstTokenMs);
        Assert.Equal(4, lines[0].Metadata!.RequestsRemaining);
        Assert.Null(lines[1].Metadata);
        Assert.Null(lines[2].Metadata);
    }

    [Fact]
    public void MetadataNormalizer_ParsesRetryAfterAndIgnoresMalformedReset()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("retry-after", "120");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "secret-or-malformed");
        var observedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var metadata = UpstreamResponseMetadataNormalizer.Normalize(response, 12, observedAt);

        Assert.Equal(TimeSpan.FromSeconds(120), metadata.RetryAfter);
        Assert.Equal(observedAt.AddSeconds(120), metadata.RetryAfterAt);
        Assert.Null(metadata.RequestsResetAt);
        Assert.Null(metadata.RequestsResetAfter);
    }

    #endregion

    #region Helpers

    private static HttpContent CreateOversizedResponseContent(bool knownLength)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', NonStreamingResponseLimitBytes + 1));
        return knownLength
            ? new ByteArrayContent(bytes)
            : new StreamContent(new ChunkedReadStream(bytes));
    }

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

    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _bytes;
        private int _offset;

        public ChunkedReadStream(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(Math.Min(count, 4096), _bytes.Length - _offset);
            if (read == 0)
                return 0;

            _bytes.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset, read));
            _offset += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(Math.Min(buffer.Length, 4096), _bytes.Length - _offset);
            if (read == 0)
                return ValueTask.FromResult(0);

            _bytes.AsMemory(_offset, read).CopyTo(buffer[..read]);
            _offset += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
