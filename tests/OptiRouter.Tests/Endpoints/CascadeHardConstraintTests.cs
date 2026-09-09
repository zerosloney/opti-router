using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Endpoints;

public sealed class CascadeHardConstraintTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Cascade_RespectsSovereigntyForVerifierAndUpgrade(bool sovereignty, bool peerVerifier)
    {
        var cheap = new ModelEndpointOptions
        {
            Name = "local-cheap", BaseUrl = "http://localhost/v1", ApiKey = "test-only",
            Tier = ModelTier.Cheap, MaxContextTokens = 8192, Enabled = true,
            IsLocalOrPrivate = true, InputPricePerMillion = 1m, OutputPricePerMillion = 1m
        };
        var strong = new ModelEndpointOptions
        {
            Name = "cloud-strong", BaseUrl = "https://example.com/v1", ApiKey = "test-only",
            Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true,
            InputPricePerMillion = 2m, OutputPricePerMillion = 2m
        };
        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(options =>
            {
                options.Models.Clear();
                options.Models.Add(cheap);
                options.Models.Add(strong);
                options.Routing.EnableRuleClassifier = true;
                options.Routing.EnableTokenEstimator = false;
                options.Routing.EnableBudgetGuard = false;
                options.Routing.EnableFailover = false;
                options.Routing.EnableSemanticRouter = false;
                options.Routing.EnableSessionAffinity = false;
                options.Routing.EnableLoadBalance = false;
                options.Routing.EnableResponseCache = false;
                options.Routing.EnableSemanticCache = false;
                options.Routing.EnableDataSovereignty = sovereignty;
                options.Routing.EnableCascadeUpgrade = true;
                options.Routing.CascadeUpgradeSampleRate = 1;
                options.Routing.CascadeUpgradeVerifierModel = peerVerifier ? strong.Name : null;
            })
        };
        int cheapVerifications = 0, cloudVerifications = 0, cloudUpgrades = 0;
        var usage = new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 };
        ChatResponse Verification() => new()
        {
            Choices = [new() { Index = 0, Message = ChatMessage.FromText("assistant", "UNCERTAIN"), FinishReason = "stop" }],
            Usage = usage
        };
        RawChatResponse Answer(string text) => new(
            System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text } } } }), usage);
        factory.MockClients[cheap.Name] = new MockModelClient(cheap,
            completeRawFunc: (_, _) => Task.FromResult(Answer("local answer")),
            completeFunc: (_, _) => { cheapVerifications++; return Task.FromResult(Verification()); });
        factory.MockClients[strong.Name] = new MockModelClient(strong,
            completeRawFunc: (_, _) => { cloudUpgrades++; return Task.FromResult(Answer("cloud answer")); },
            completeFunc: (_, _) => { cloudVerifications++; return Task.FromResult(Verification()); });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new ChatRequest
        {
            Model = "auto", Messages = [ChatMessage.FromText("user", "Hi")]
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sovereignty || !peerVerifier ? 1 : 0, cheapVerifications);
        Assert.Equal(!sovereignty && peerVerifier ? 1 : 0, cloudVerifications);
        Assert.Equal(sovereignty ? 0 : 1, cloudUpgrades);
        Assert.Contains(sovereignty ? "local answer" : "cloud answer", await response.Content.ReadAsStringAsync());
        decimal expectedCost = (3m + (sovereignty || !peerVerifier ? 3m : 6m) + (sovereignty ? 0m : 6m)) / 1_000_000m;
        Assert.Equal(expectedCost, factory.Services.GetRequiredService<CostLedger>().GetDailySpend());
    }
}
