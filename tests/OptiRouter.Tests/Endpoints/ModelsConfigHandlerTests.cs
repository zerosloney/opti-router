using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Endpoints;

public class ModelsConfigHandlerTests
{
    private sealed class ModelsFactory : WebApplicationFactory<Program>
    {
        public const string Key = "models-test-key";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "optirouter-models-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempRoot);
            File.Copy(FindSourceAppsettings(), Path.Combine(_tempRoot, "appsettings.json"));
            builder.UseSetting(WebHostDefaults.ContentRootKey, _tempRoot);
            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:ConfigDbPath", Path.Combine(_tempRoot, "optirouter-config.db"));
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(options =>
                {
                    options.Models.Clear();
                    options.Models.Add(new ModelEndpointOptions
                    {
                        Name = "test-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    });
                    options.Routing.EnableHealthProbe = false;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_tempRoot))
            {
                try { Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
        }

        // 源 appsettings.json 由测试项目复制到输出目录（见 OptiRouter.Tests.csproj），不向上遍历目录树。
        private static string FindSourceAppsettings()
            => Path.Combine(AppContext.BaseDirectory, "RepositoryFiles", "appsettings.json");
    }

    [Fact]
    public async Task CreateModel_MissingOrNegativePrices_AreZero()
    {
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        string nullInputName = "null-input-" + Guid.NewGuid().ToString("N");
        string nullOutputName = "null-output-" + Guid.NewGuid().ToString("N");
        var payloads = new Dictionary<string, string>
        {
            [nullInputName] = "{\"name\":\"" + nullInputName + "\",\"baseUrl\":\"https://example.com\",\"inputPricePerMillion\":null,\"outputPricePerMillion\":-2}",
            [nullOutputName] = "{\"name\":\"" + nullOutputName + "\",\"baseUrl\":\"https://example.com\",\"inputPricePerMillion\":-1,\"outputPricePerMillion\":null}"
        };

        foreach (var payload in payloads.Values)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/models", content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var listResponse = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var document = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        foreach (string name in payloads.Keys)
        {
            var model = document.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name);
            Assert.Equal(0, model.GetProperty("inputPricePerMillion").GetDecimal());
            Assert.Equal(0, model.GetProperty("outputPricePerMillion").GetDecimal());
        }
    }
}
