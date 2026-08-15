using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ProviderInferenceTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/v1", "deepseek")]
    [InlineData("https://api.openai.com/v1", "openai")]
    [InlineData("https://api.anthropic.com/v1", "anthropic")]
    [InlineData("https://generativelanguage.googleapis.com/v1", "google")]
    [InlineData("https://api.moonshot.cn/v1", "moonshot")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "zhipu")]
    [InlineData("https://api.siliconflow.cn/v1", "siliconflow")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "aliyun")]
    [InlineData("https://openrouter.ai/api/v1", "openrouter")]
    public void KnownHosts_Inferred(string baseUrl, string expected)
    {
        Assert.Equal(expected, ProviderInference.Infer(baseUrl));
    }

    [Theory]
    [InlineData("http://localhost:8000/v1", "local")]
    [InlineData("http://127.0.0.1:11434/v1", "local")]
    [InlineData("http://10.1.2.3/v1", "local")]
    [InlineData("http://192.168.1.10/v1", "local")]
    [InlineData("http://172.16.5.4/v1", "local")]
    public void LocalAndPrivateHosts_ReturnLocal(string baseUrl, string expected)
    {
        Assert.Equal(expected, ProviderInference.Infer(baseUrl));
    }

    [Fact]
    public void UnknownHost_FallsBackToRegistrableDomain()
    {
        Assert.Equal("mycompany.com", ProviderInference.Infer("https://llm.mycompany.com/v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    public void InvalidBaseUrl_ReturnsEmpty(string baseUrl)
    {
        Assert.Equal(string.Empty, ProviderInference.Infer(baseUrl));
    }
}
