using System.Net;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.ClientTests;

public sealed class ProviderAdapterSandboxTests
{
    private readonly ProviderAdapterSandbox _sandbox = new();

    [Fact]
    public void GetSupportedProviders_ReturnsExpectedList()
    {
        var providers = _sandbox.GetSupportedProviders();
        Assert.NotEmpty(providers);
        Assert.Contains("openai", providers);
        Assert.Contains("deepseek", providers);
        Assert.Contains("ollama", providers);
        Assert.Contains("vllm", providers);
    }

    [Fact]
    public async Task ValidateEndpointAsync_EmptyBaseUrl_ReturnsInvalid()
    {
        var endpoint = new ModelEndpointOptions
        {
            Id = "m1",
            Name = "empty-url",
            BaseUrl = ""
        };

        var result = await _sandbox.ValidateEndpointAsync(endpoint);

        Assert.False(result.IsValid);
        Assert.Equal("BaseUrl cannot be empty.", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateEndpointAsync_NullEndpoint_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sandbox.ValidateEndpointAsync(null!));
    }

    [Fact]
    public async Task ValidateEndpointAsync_UsesProtocolSpecificAuthenticationHeaders()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var sandbox = new ProviderAdapterSandbox(httpClient);

        var anthropic = CreateEndpoint(ProviderProtocol.Anthropic);
        var result = await sandbox.ValidateEndpointAsync(anthropic);
        Assert.True(result.IsValid);
        Assert.Null(handler.Authorization);
        Assert.Equal("sk-test", handler.ApiKey);
        Assert.Equal("2023-06-01", handler.AnthropicVersion);
        Assert.Null(handler.GeminiApiKey);

        var gemini = CreateEndpoint(ProviderProtocol.Gemini);
        result = await sandbox.ValidateEndpointAsync(gemini);
        Assert.True(result.IsValid);
        Assert.Null(handler.Authorization);
        Assert.Null(handler.ApiKey);
        Assert.Null(handler.AnthropicVersion);
        Assert.Equal("sk-test", handler.GeminiApiKey);

        var openAi = CreateEndpoint(ProviderProtocol.OpenAI);
        result = await sandbox.ValidateEndpointAsync(openAi);
        Assert.True(result.IsValid);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        Assert.Null(handler.ApiKey);
        Assert.Null(handler.AnthropicVersion);
        Assert.Null(handler.GeminiApiKey);

        openAi.ApiKey = " ";
        result = await sandbox.ValidateEndpointAsync(openAi);
        Assert.True(result.IsValid);
        Assert.Null(handler.Authorization);
        Assert.Null(handler.ApiKey);
        Assert.Null(handler.AnthropicVersion);
        Assert.Null(handler.GeminiApiKey);
    }

    private static ModelEndpointOptions CreateEndpoint(ProviderProtocol protocol) => new()
    {
        Id = "m1",
        Name = "m1",
        BaseUrl = "https://example.test/v1",
        ApiKey = "sk-test",
        Protocol = protocol
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        public string? GeminiApiKey { get; private set; }
        public string? AnthropicVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = GetHeader(request, "x-api-key");
            GeminiApiKey = GetHeader(request, "x-goog-api-key");
            AnthropicVersion = GetHeader(request, "anthropic-version");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static string? GetHeader(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }
}
