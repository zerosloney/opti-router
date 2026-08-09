using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OptiRouter.Tests.Components;

public class UiStaticAssetsTests
{
    private sealed class UiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.UseSetting("OptiRouter:ProxyApiKey", "ui-test-key");
            builder.UseSetting("OptiRouter:AdminApiKey", "ui-test-key");
            builder.UseSetting("OptiRouter:Routing:EnableHealthProbe", "false");
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
