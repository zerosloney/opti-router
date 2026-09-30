using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// /v1/systemone 原生透传端点集成测试：走完整 HTTP 管道（租户鉴权 → 协议校验 → 模型寻址 → 上游转发），
/// 上游用注入 IHttpClientFactory 的 stub handler 模拟。
/// </summary>
public class SystemOneEndpointTests
{
    private const string UpstreamKey = "sk-unisound-test";

    /// <summary>上游 stub：捕获最后一次请求（URL/Authorization/body），返回可配置的状态码与响应体。</summary>
    private sealed class StubUpstreamHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public HttpStatusCode ResponseStatusCode = HttpStatusCode.OK;
        public string ResponseBody =
            "{\"model\":\"u2-decision\",\"answers\":{\"q1\":{\"choice\":\"a\",\"probabilities\":{\"a\":0.9,\"b\":0.1}}},\"latency_ms\":12}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private static ModelEndpointOptions CreateSystemOneEndpoint() => new()
    {
        Name = "u2-decision",
        Id = "u2-decision",
        BaseUrl = "https://upstream.test/v1",
        ApiKey = UpstreamKey,
        Protocol = ProviderProtocol.SystemOne,
        Tier = ModelTier.Medium,
        InputPricePerMillion = 1m,
        Enabled = true
    };

    private static (TestWebApplicationFactory Factory, StubUpstreamHandler Stub) CreateFactory(
        ModelEndpointOptions? model = null)
    {
        var factory = new TestWebApplicationFactory();
        var stub = new StubUpstreamHandler();
        var endpoint = model ?? CreateSystemOneEndpoint();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(endpoint);
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
            services.AddHttpClient(SystemOneEndpoint.UpstreamHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => stub);
        };
        return (factory, stub);
    }

    private static string BuildBody(string model = "\"u2-decision\"", bool includeQuestions = true)
    {
        var sb = new StringBuilder("{\"model\":");
        sb.Append(model);
        sb.Append(",\"state\":\"工单文本\"");
        if (includeQuestions)
            sb.Append(",\"questions\":{\"q1\":{\"type\":\"choice\",\"options\":[\"a\",\"b\"]}}");
        sb.Append('}');
        return sb.ToString();
    }

    [Fact]
    public async Task Post_ForwardsWithUpstreamAuth_OverridesModelAndPath_ReturnsUpstreamBodyVerbatim()
    {
        var (factory, stub) = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(stub.ResponseBody, await response.Content.ReadAsStringAsync());

        Assert.NotNull(stub.LastRequest);
        // BaseUrl 已带 /v1 段时不重复拼接：https://upstream.test/v1/systemone
        Assert.Equal("https://upstream.test/v1/systemone", stub.LastRequest!.RequestUri!.AbsoluteUri);
        // 上游鉴权 = 端点配置的 ApiKey，而非客户端租户 key
        Assert.Equal($"Bearer {UpstreamKey}", stub.LastRequest.Headers.Authorization!.ToString());
        // model 覆写为 UpstreamModelId（此处同名）；state/questions 保真
        Assert.Contains("\"model\":\"u2-decision\"", stub.LastBody);
        Assert.Contains("\"state\":\"工单文本\"", stub.LastBody);
        Assert.Contains("\"questions\":", stub.LastBody);
    }

    [Fact]
    public async Task Post_WithoutModel_ResolveSingleSystemOneEndpoint()
    {
        var (factory, stub) = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(model: "null"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 缺省 model 时唯一 SystemOne 端点自动命中，且发往上游前补写 model
        Assert.NotNull(stub.LastRequest);
        Assert.Contains("\"model\":\"u2-decision\"", stub.LastBody);
    }

    [Fact]
    public async Task Post_MissingQuestions_Returns400()
    {
        var (factory, stub) = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(includeQuestions: false), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("questions", await response.Content.ReadAsStringAsync());
        Assert.Null(stub.LastRequest);
    }

    [Fact]
    public async Task Post_UnknownModel_Returns404ModelNotFound()
    {
        var (factory, stub) = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(model: "\"nope\""), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("model_not_found", await response.Content.ReadAsStringAsync());
        Assert.Null(stub.LastRequest);
    }

    [Fact]
    public async Task Post_UpstreamNon2xx_PassthroughStatusAndBody()
    {
        var (factory, stub) = CreateFactory();
        stub.ResponseStatusCode = HttpStatusCode.TooManyRequests;
        stub.ResponseBody = "{\"error\":{\"message\":\"rate limited\"}}";
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(), Encoding.UTF8, "application/json"));

        // 上游错误状态与响应体原样透传，不二次包装
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(stub.ResponseBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_WithoutClientKey_Returns401BeforeCallingUpstream()
    {
        var (factory, stub) = CreateFactory();
        var client = factory.CreateClient(null); // 不带 Authorization

        var response = await client.PostAsync("/v1/systemone",
            new StringContent(BuildBody(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(stub.LastRequest);
    }

    [Fact]
    public async Task Post_ChatPinnedSystemOneModel_Returns404OnChatEndpoint()
    {
        var (factory, _) = CreateFactory();
        var client = factory.CreateClient();

        // SystemOne 端点对 chat 契约不可见：显式点名走 /v1/chat/completions 应 404 而非 503
        var response = await client.PostAsync("/v1/chat/completions",
            new StringContent(
                "{\"model\":\"u2-decision\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("model_not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ManagementApi_CreateModelWithSystemOneProtocol_PersistsAndRoundTrips()
    {
        const string adminKey = "test-admin-key";
        var (factory, _) = CreateFactory();
        factory.AdminApiKey = adminKey;
        var adminClient = factory.CreateClient(adminKey);

        var create = await adminClient.PostAsync("/api/models",
            new StringContent(
                "{\"name\":\"decision-2\",\"id\":\"u2-decision\",\"baseUrl\":\"https://upstream2.test\",\"apiKey\":\"sk-x\",\"protocol\":\"SystemOne\"}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 创建后再 PUT 更新其他字段（protocol 字段不传），protocol 不得被静默回退为 OpenAI
        var update = await adminClient.PutAsync("/api/models/decision-2",
            new StringContent("{\"maxContextTokens\":4096}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Contains("\"protocol\":\"SystemOne\"", await update.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ManagementApi_CreateModelWithUnknownProtocol_Returns400()
    {
        const string adminKey = "test-admin-key";
        var (factory, _) = CreateFactory();
        factory.AdminApiKey = adminKey;
        var adminClient = factory.CreateClient(adminKey);

        var create = await adminClient.PostAsync("/api/models",
            new StringContent(
                "{\"name\":\"bad-proto\",\"baseUrl\":\"https://upstream3.test\",\"protocol\":\"OpenAI2\"}",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    // ── 纯单元：路由候选池与 chat 已知模型排除 ──

    private static RouterOptions BuildOptions(params ModelEndpointOptions[] models)
    {
        var options = new RouterOptions();
        foreach (var m in models) options.Models.Add(m);
        return options;
    }

    [Fact]
    public void RouterEngine_Decide_ExcludesSystemOneFromChatCandidates()
    {
        var systemOne = CreateSystemOneEndpoint();
        var chatModel = new ModelEndpointOptions
        {
            Name = "chat-model", BaseUrl = "https://chat.test/v1", Enabled = true, Tier = ModelTier.Medium
        };
        var engine = new RouterEngine(new CostLedger(), Enumerable.Empty<IRouterPolicy>());
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") }
        };

        var decision = engine.Decide(request, BuildOptions(systemOne, chatModel));

        Assert.Contains(decision.Candidates, c => c.Name == "chat-model");
        Assert.DoesNotContain(decision.Candidates, c => c.Protocol == ProviderProtocol.SystemOne);
    }

    [Fact]
    public void IsKnownModel_SystemOneEndpointsInvisibleToChatContracts()
    {
        var options = BuildOptions(CreateSystemOneEndpoint());
        Assert.False(ChatCompletionsEndpoint.IsKnownModel("u2-decision", options));
        Assert.False(ChatCompletionsEndpoint.IsKnownModel("unisound/u2-decision", options));
    }

    [Fact]
    public void ModelClientFactory_SystemOneProtocol_ReturnsSystemOneModelClient()
    {
        var client = ModelClientFactory.CreateForEndpoint(CreateSystemOneEndpoint(), new StubUpstreamHandler());
        Assert.IsType<SystemOneModelClient>(client);
    }

    [Fact]
    public void ResolveApiPath_HandlesV1StyleBaseUrl()
    {
        Assert.Equal("https://maas-api.unisound.com/v1/systemone",
            SystemOneModelClient.ResolveApiPath("https://maas-api.unisound.com", "systemone"));
        Assert.Equal("https://maas-api.unisound.com/v1/systemone",
            SystemOneModelClient.ResolveApiPath("https://maas-api.unisound.com/v1", "systemone"));
        Assert.Equal("https://maas-api.unisound.com/v1/systemone",
            SystemOneModelClient.ResolveApiPath("https://maas-api.unisound.com/v1/", "systemone"));
    }
}
