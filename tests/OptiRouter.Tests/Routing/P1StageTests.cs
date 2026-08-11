using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class P1StageTests
{
    [Fact]
    public void JsonAstRepairer_RepairsCodeFenceAndTrailingCommas()
    {
        string raw = "Here is the result:\n```json\n{\n  \"name\": \"test\",\n  \"items\": [1, 2, 3,],\n}\n```\nHope this helps!";
        string repaired = JsonAstRepairer.RepairJson(raw);

        Assert.DoesNotContain("Here is the result:", repaired);
        Assert.DoesNotContain("```", repaired);
        Assert.True(JsonAstRepairer.TryParse(repaired, out var doc));
        Assert.NotNull(doc);
        Assert.Equal("test", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void JsonAstRepairer_AutoClosesTruncatedJson()
    {
        string truncated = "{\"status\": \"ok\", \"data\": [{\"id\": 10";
        string repaired = JsonAstRepairer.RepairJson(truncated);

        Assert.True(JsonAstRepairer.TryParse(repaired, out var doc));
        Assert.NotNull(doc);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void PiiAnonymizer_AnonymizesAndDeanonymizesRequest()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "My phone is 13812345678 and my email is user@example.com.")
            }
        };

        var (sanitized, piiMap, containsPii) = PiiAnonymizer.AnonymizeRequest(request);

        Assert.True(containsPii);
        Assert.True(piiMap.HasSensitiveData);

        string sanitizedText = sanitized.Messages![0].GetText();
        Assert.DoesNotContain("13812345678", sanitizedText);
        Assert.DoesNotContain("user@example.com", sanitizedText);
        Assert.Contains("[PII_PHONE_1]", sanitizedText);
        Assert.Contains("[PII_EMAIL_1]", sanitizedText);

        string modelResponse = "Received data for [PII_PHONE_1] and [PII_EMAIL_1].";
        string restored = PiiAnonymizer.DeanonymizeText(modelResponse, piiMap);

        Assert.Contains("13812345678", restored);
        Assert.Contains("user@example.com", restored);
    }

    [Fact]
    public void DataSovereigntyPolicy_ExcludesCloudEndpointsWhenEnabled()
    {
        var policy = new DataSovereigntyPolicy();
        var options = new RouterOptions
        {
            Routing = new RoutingOptions { EnableDataSovereignty = true }
        };

        var context = new RouterContext
        {
            Request = new ChatRequest(),
            AllModels = new List<ModelEndpointOptions>(),
            Options = options
        };

        var previous = new RouterDecision
        {
            Reason = "initial",
            Candidates = new List<ModelEndpointOptions>
            {
                new ModelEndpointOptions { Name = "cloud-gpt4", IsLocalOrPrivate = false },
                new ModelEndpointOptions { Name = "local-ollama", IsLocalOrPrivate = true },
                new ModelEndpointOptions { Name = "cloud-claude", IsLocalOrPrivate = false, Tags = new List<string> { "local" } }
            }
        };

        var result = policy.Apply(context, previous);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, m => m.Name == "local-ollama");
        Assert.Contains(result.Candidates, m => m.Name == "cloud-claude");
        Assert.DoesNotContain(result.Candidates, m => m.Name == "cloud-gpt4");
    }
}
