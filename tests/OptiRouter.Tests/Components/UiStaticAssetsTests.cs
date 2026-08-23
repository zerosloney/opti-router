using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Components;

public class UiStaticAssetsTests
{
    private sealed class UiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.UseSetting("OptiRouter:AdminApiKey", "ui-test-key");
            builder.UseSetting("OptiRouter:Routing:EnableHealthProbe", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("ui-test-key");
                // 覆盖 Program 的权威文件配置，测试不依赖工作区中的 models-config.json。
                services.Configure<RouterOptions>(options =>
                {
                    options.Models.Clear();
                    options.Models.Add(new ModelEndpointOptions
                    {
                        Name = "ui-test-model",
                        BaseUrl = "http://localhost/v1",
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        TimeoutSeconds = 30,
                        MaxRetries = 0,
                        Enabled = true
                    });
                });
            });
        }
    }

    [Theory]
    [InlineData("/css/blazor.css", "text/css")]
    [InlineData("/_framework/blazor.server.js", "javascript")]
    public async Task BlazorStaticAsset_IsServed(string path, string mediaTypeFragment)
    {
        using var factory = new UiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(mediaTypeFragment, response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }
}
