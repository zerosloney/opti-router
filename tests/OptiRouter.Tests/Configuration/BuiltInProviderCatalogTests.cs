using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// 内置 provider catalog 注入开关回归：目录为第三方端点，注入即改变 prompt 数据流向，
/// 默认必须关闭（空配置不注入）；显式开启后注入且用户已配置模型时不覆盖。
/// </summary>
public class BuiltInProviderCatalogTests
{
    private sealed class CatalogFactory : WebApplicationFactory<Program>
    {
        public bool EnableCatalog { get; set; }
        public Action<IServiceCollection>? ExtraConfigure { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.UseSetting(
                "OptiRouter:ConfigDbPath",
                Path.Combine(Path.GetTempPath(), "optirouter-config-test-" + Guid.NewGuid().ToString("N") + ".db"));
            builder.UseSetting("OptiRouter:EnableSingleInstanceGuard", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Routing.EnableBuiltInProviderCatalog = EnableCatalog;
                });
                ExtraConfigure?.Invoke(services);
            });
        }
    }

    private IList<ModelEndpointOptions> ResolveModels(CatalogFactory factory) =>
        factory.Services.GetRequiredService<IOptions<RouterOptions>>().Value.Models;

    [Fact]
    public void EmptyModels_CatalogDisabledByDefault_DoesNotInject()
    {
        // 默认（开关 false）：配置库为空不注入任何第三方端点——RouterOptions 的
        // "Models 不能为空"校验随之生效（若被注入则校验通过），异常即不注入的证明。
        using var factory = new CatalogFactory { EnableCatalog = false };

        Assert.Throws<OptionsValidationException>(() => ResolveModels(factory));
    }

    [Fact]
    public void EmptyModels_CatalogEnabled_InjectsDefaults()
    {
        using var factory = new CatalogFactory { EnableCatalog = true };

        var models = ResolveModels(factory);

        Assert.NotEmpty(models);
        Assert.Contains(models, m => m.Name == "kimi/kimi-k3");
        Assert.Contains(models, m => m.Name == "deepseek/deepseek-v4-flash");
        Assert.All(models, m => Assert.True(m.Enabled));
    }

    [Fact]
    public void UserModelsPresent_CatalogEnabled_DoesNotOverride()
    {
        // 用户已配置模型时优先级高于 catalog：不注入、不覆盖。
        using var factory = new CatalogFactory
        {
            EnableCatalog = true,
            ExtraConfigure = services => services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "my-model",
                    BaseUrl = "https://example.com",
                    ApiKey = "k",
                    Enabled = true
                });
            })
        };

        var models = ResolveModels(factory);

        var model = Assert.Single(models);
        Assert.Equal("my-model", model.Name);
    }
}
