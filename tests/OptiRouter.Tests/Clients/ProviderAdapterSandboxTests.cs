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
}
