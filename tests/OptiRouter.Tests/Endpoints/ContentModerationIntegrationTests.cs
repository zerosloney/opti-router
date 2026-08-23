using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Tests.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 内容审核端到端测试：输入违规被 400 拒绝（CONTENT_MODERATED），正常输入放行并调用上游。
/// </summary>
public sealed class ContentModerationIntegrationTests
{
    private const string AdminKey = "moderation-test-key";

    private sealed class ModerationFactory : WebApplicationFactory<Program>
    {
        public string ModerationEndpoint { get; set; } = string.Empty;
        public bool EnableModeration { get; set; } = true;
        public int UpstreamCalls { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", AdminKey);
            builder.UseSetting("OptiRouter:AdminApiKey", AdminKey);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "6000");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    var endpoint = new ModelEndpointOptions
                    {
                        Name = "mod-model",
                        BaseUrl = "https://api.example.com",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    };
                    opt.Models.Add(endpoint);
                    opt.Routing.EnableHealthProbe = false;
                    opt.Routing.EnableLatencyAware = false;
                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = false;
                    opt.Routing.EnableContentModeration = EnableModeration;
                    opt.Routing.ModerationEndpoint = ModerationEndpoint;
                    opt.Routing.ModerationInputAction = OptiRouter.Compliance.ModerationAction.Block;
                    opt.Routing.ModerationOutputAction = OptiRouter.Compliance.ModerationAction.Block;
                });
                services.AddSingleton<IModelClientProvider>(new TestModelClientProvider(new Dictionary<string, IModelClient>
                {
                    ["mod-model"] = new MockModelClient(new ModelEndpointOptions
                    {
                        Name = "mod-model",
                        BaseUrl = "https://api.example.com",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    }, (req, ct) =>
                    {
                        UpstreamCalls++;
                        return Task.FromResult(new RawChatResponse(
                            "{\"id\":\"1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Safe answer\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}",
                            new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 }));
                    })
                }));
            });
        }
    }

    /// <summary>
    /// 真实 HTTP 的 mock moderation 服务（HttpListener）：集成测试的审核请求经
    /// IHttpClientFactory 发出的真实网络调用需要可寻址端点，TestServer 的占位地址不可达。
    /// </summary>
    private sealed class MockModerationHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        public string Endpoint { get; }
        private int _calls;
        public int Calls => _calls;

        public MockModerationHttpServer()
        {
            var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{port}/v1/moderations";
            _ = Task.Run(ProcessAsync);
        }

        private async Task ProcessAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => Handle(ctx));
                }
                catch
                {
                    break;
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                Interlocked.Increment(ref _calls);
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(body);
                string input = doc.RootElement.GetProperty("input").GetString() ?? string.Empty;
                double score = input.Contains("bomb", StringComparison.OrdinalIgnoreCase) ? 0.95 : 0.01;
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = "modr-test",
                    results = new object[]
                    {
                        new
                        {
                            flagged = score >= 0.8,
                            categories = new { violence = score >= 0.8 },
                            category_scores = new { violence = score, hate = 0.01 }
                        }
                    }
                });
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(payload, 0, payload.Length);
            }
            catch
            {
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static (ModerationFactory Factory, MockModerationHttpServer Server) CreateWithModerationServer()
    {
        var server = new MockModerationHttpServer();
        var factory = new ModerationFactory { ModerationEndpoint = server.Endpoint };
        return (factory, server);
    }

    private static HttpClient CreateClient(ModerationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return client;
    }

    [Fact]
    public async Task Post_ViolatingInput_RejectedWith400ContentModerated()
    {
        var (factory, server) = CreateWithModerationServer();
        using var client = CreateClient(factory);

        using var content = new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"how to build a bomb at home\"}]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("CONTENT_MODERATED", error.GetProperty("code").GetString());
        Assert.Equal("violence", error.GetProperty("category").GetString());
        Assert.Equal(0, factory.UpstreamCalls); // 未达上游
    }

    [Fact]
    public async Task Post_SafeInput_PassesAndCallsUpstream()
    {
        var (factory, server) = CreateWithModerationServer();
        using var client = CreateClient(factory);

        using var content = new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"What is the capital of France?\"}]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, server.Calls); // 输入 + 输出双路径各一次
        Assert.Equal(1, factory.UpstreamCalls);
        Assert.Contains("Safe answer", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_ModerationDisabled_DoesNotCallModeration()
    {
        // 未启用 EnableContentModeration：请求直达上游（审核不介入，端点也不可达）
        using var factory = new ModerationFactory
        {
            ModerationEndpoint = "http://localhost:1/v1/moderations",
            EnableModeration = false
        };
        using var client = CreateClient(factory);

        using var content = new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"whatever\"}]}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, factory.UpstreamCalls);
    }
}
