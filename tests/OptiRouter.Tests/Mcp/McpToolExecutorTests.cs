using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OptiRouter.Mcp;
using Xunit;

namespace OptiRouter.Tests.Mcp;

/// <summary>
/// MCP Streamable HTTP 执行器测试：用 TestServer 模拟 MCP Server，验证
/// initialize → initialized → tools/call 的 JSON-RPC 协议流程、鉴权头与会话头复用。
/// </summary>
public sealed class McpToolExecutorTests
{
    private sealed record CapturedRequest(string Method, string Body, string SessionHeader, string? Authorization);

    private sealed class OversizedResponseHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public TrackingContent? LastContent { get; private set; }

        public OversizedResponseHandler()
        {
            string padding = new('x', OptiRouter.Clients.BoundedResponseReader.MaxNonStreamingResponseBytes + 1);
            _body = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"padding\":\"{padding}\"}}}}");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastContent = new TrackingContent(_body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = LastContent
            });
        }
    }

    private sealed class TrackingContent(byte[] body) : HttpContent
    {
        private int _serializeCalls;

        public int SerializeCalls => Volatile.Read(ref _serializeCalls);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            Interlocked.Increment(ref _serializeCalls);
            await stream.WriteAsync(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(body, writable: false));
    }

    private static async Task<(McpToolExecutor Executor, List<CapturedRequest> Requests)> StartMockServerAsync(
        Func<string, string>? toolsCallResponder = null)
    {
        var requests = new List<CapturedRequest>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            string session = ctx.Request.Headers["Mcp-Session-Id"].ToString();
            string? auth = ctx.Request.Headers.Authorization.ToString();

            using var doc = JsonDocument.Parse(body);
            string method = doc.RootElement.GetProperty("method").GetString() ?? string.Empty;
            var id = doc.RootElement.GetProperty("id");
            requests.Add(new CapturedRequest(method, body, session, auth));

            if (method == "initialize")
            {
                ctx.Response.Headers["Mcp-Session-Id"] = "sess-abc-123";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "mock-server", version = "1.0" }
                    }
                });
            }
            else if (method == "notifications/initialized")
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            }
            else if (method == "tools/call")
            {
                string name = doc.RootElement.GetProperty("params").GetProperty("name").GetString() ?? string.Empty;
                string response = toolsCallResponder?.Invoke(name) ?? JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = $"result-of-{name}" },
                            new { type = "text", text = " second-part" }
                        },
                        isError = false
                    }
                });
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(response);
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        await app.StartAsync();
        var client = app.GetTestClient();
        var executor = new McpToolExecutor(client);
        return (executor, requests);
    }

    private static McpServerRegistration CreateServer(string baseUrl = "http://localhost/mcp", string? apiKey = "sk-mcp-test")
    {
        return new McpServerRegistration
        {
            Name = "test-server",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            TimeoutMs = 5000
        };
    }

    [Fact]
    public async Task ExecuteToolAsync_FollowsProtocolHandshakeAndReturnsContent()
    {
        var (executor, requests) = await StartMockServerAsync();

        var result = await executor.ExecuteToolAsync(
            CreateServer(),
            "get_weather",
            JsonSerializer.Deserialize<JsonElement>("{\"city\":\"Beijing\"}"));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("result-of-get_weather\n second-part", result.Content);

        // 协议顺序：initialize → notifications/initialized → tools/call
        Assert.Equal(3, requests.Count);
        Assert.Equal("initialize", requests[0].Method);
        Assert.Equal("notifications/initialized", requests[1].Method);
        Assert.Equal("tools/call", requests[2].Method);

        // initialize 携带协议版本与客户端信息
        Assert.Contains("\"protocolVersion\":\"2025-03-26\"", requests[0].Body);
        Assert.Contains("\"clientInfo\"", requests[0].Body);
        Assert.Contains("\"OptiRouter\"", requests[0].Body);

        // 握手后会话头随后续请求复用
        Assert.Equal("", requests[0].SessionHeader);
        Assert.Equal("sess-abc-123", requests[1].SessionHeader);
        Assert.Equal("sess-abc-123", requests[2].SessionHeader);

        // 鉴权头在所有请求上携带
        Assert.All(requests, r => Assert.Equal("Bearer sk-mcp-test", r.Authorization));

        // tools/call 携带工具名与参数
        Assert.Contains("\"name\":\"get_weather\"", requests[2].Body);
        Assert.Contains("\"city\":\"Beijing\"", requests[2].Body);
    }

    [Fact]
    public async Task ExecuteToolAsync_JsonRpcError_ReturnsFailure()
    {
        var (executor, requests) = await StartMockServerAsync(name =>
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                error = new { code = -32602, message = "Invalid params: unknown tool" }
            }));

        var result = await executor.ExecuteToolAsync(CreateServer(), "bad_tool", default);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid params: unknown tool", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_ToolIsError_ReturnsFailureWithContent()
    {
        var (executor, _) = await StartMockServerAsync(name =>
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new object[] { new { type = "text", text = "execution failed" } },
                    isError = true
                }
            }));

        var result = await executor.ExecuteToolAsync(CreateServer(), "failing_tool", default);

        Assert.False(result.IsSuccess);
        Assert.Equal("execution failed", result.Content);
    }

    [Fact]
    public async Task ExecuteToolAsync_Timeout_ReturnsFailure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
        });
        await app.StartAsync();

        var executor = new McpToolExecutor(app.GetTestClient());
        var server = new McpServerRegistration
        {
            Name = "slow-server",
            BaseUrl = "http://localhost/mcp",
            TimeoutMs = 200
        };

        var result = await executor.ExecuteToolAsync(server, "slow_tool", default);

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_NonSuccessHttpStatus_ReturnsFailure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/mcp", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await app.StartAsync();

        var executor = new McpToolExecutor(app.GetTestClient());
        var result = await executor.ExecuteToolAsync(CreateServer(), "boom", default);

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_OversizedResponse_StreamsBeforeBuffering()
    {
        var handler = new OversizedResponseHandler();
        using var client = new HttpClient(handler);
        var executor = new McpToolExecutor(client);

        var result = await executor.ExecuteToolAsync(CreateServer(), "large_tool", default);

        Assert.False(result.IsSuccess);
        Assert.Contains("exceeded", result.ErrorMessage);
        Assert.NotNull(handler.LastContent);
        Assert.Equal(0, handler.LastContent!.SerializeCalls);
    }
}
