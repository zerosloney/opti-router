using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace OptiRouter.Tests.Smoke;

/// <summary>
/// 端到端冒烟测试：用 WireMock.Net 起真实 HTTP mock server 模拟上游模型 API，
/// 验证 OpenAICompatibleModelClient 真的发 HTTP 出去并正确回传。
/// </summary>
public class EndToEndSmokeTests : IDisposable
{
    private readonly WireMockServer _wireMock;
    private readonly int _port;

    public EndToEndSmokeTests()
    {
        _wireMock = WireMockServer.Start();
        _port = _wireMock.Port;
    }

    public void Dispose()
    {
        _wireMock.Stop();
        _wireMock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ChatRequest BuildRequest(string model, bool stream = false)
    {
        return new ChatRequest
        {
            Model = model,
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "Hi" } },
            Stream = stream
        };
    }

    private SmokeWebApplicationFactory CreateFactory(params (string Name, ModelTier Tier, decimal InputPrice)[] models)
    {
        var factory = new SmokeWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                foreach (var (name, tier, price) in models)
                {
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = name,
                        BaseUrl = $"http://localhost:{_port}/v1",
                        ApiKey = "test-key",
                        Tier = tier,
                        MaxContextTokens = 32000,
                        InputPricePerMillion = price,
                        OutputPricePerMillion = price * 2,
                        Enabled = true
                    });
                }
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };
        return factory;
    }

    [Fact]
    public async Task NonStreaming_EndToEnd_Returns200AndRecordsCost()
    {
        // Arrange: WireMock 模拟 model-a 返回标准 OpenAI 非流式响应
        _wireMock.Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"chatcmpl-wm\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hello from WireMock\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}"));

        using var factory = CreateFactory(("model-a", ModelTier.Medium, 1m));
        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-a", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello from WireMock", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Session > 0, "Expected cost to be recorded after successful request.");
    }

    [Fact]
    public async Task Streaming_EndToEnd_ReturnsSseEventsAndDone()
    {
        // Arrange: WireMock 模拟 model-a 返回 SSE 流
        var sseBody = string.Join("\n\n", new[]
        {
            "data: {\"id\":\"chatcmpl-wm\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}",
            "data: {\"id\":\"chatcmpl-wm\",\"model\":\"model-a\",\"choices\":[{\"index\":1,\"delta\":{\"content\":\"!\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}",
            "data: [DONE]"
        }) + "\n\n";

        _wireMock.Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(sseBody));

        using var factory = CreateFactory(("model-a", ModelTier.Medium, 1m));
        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        var dataLines = new List<string>();
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]")
            {
                dataLines.Add("[DONE]");
                break;
            }
            dataLines.Add(data);
        }

        Assert.NotEmpty(dataLines);
        Assert.Equal("[DONE]", dataLines[^1]);

        using var firstDoc = JsonDocument.Parse(dataLines[0]);
        Assert.Equal("chatcmpl-wm", firstDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Hello", firstDoc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        // 流式最后一块带 usage，记账后 ledger 应 > 0
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Session > 0, "Expected streaming cost to be recorded from final chunk usage.");
    }

    [Fact]
    public async Task Failover_EndToEnd_Primary500_FallsBackToNext()
    {
        // Arrange: WireMock 让 model-a 返回 500，model-b 返回 200
        // 用不同 Authorization header 区分两个模型的请求
        _wireMock.Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost()
                .WithHeader("Authorization", "Bearer test-key-a"))
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\":{\"message\":\"model-a boom\"}}"));

        _wireMock.Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost()
                .WithHeader("Authorization", "Bearer test-key-b"))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"chatcmpl-wm-b\",\"model\":\"model-b\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"From B\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}"));

        var factory = new SmokeWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "model-a",
                    BaseUrl = $"http://localhost:{_port}/v1",
                    ApiKey = "test-key-a",
                    Tier = ModelTier.Strong,
                    MaxContextTokens = 32000,
                    InputPricePerMillion = 5m,
                    OutputPricePerMillion = 10m,
                    Enabled = true
                });
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "model-b",
                    BaseUrl = $"http://localhost:{_port}/v1",
                    ApiKey = "test-key-b",
                    Tier = ModelTier.Medium,
                    MaxContextTokens = 32000,
                    InputPricePerMillion = 1m,
                    OutputPricePerMillion = 2m,
                    Enabled = true
                });
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = true;
            });
        };

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("From B", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }
}
