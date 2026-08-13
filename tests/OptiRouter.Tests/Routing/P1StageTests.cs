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
    public void PiiAnonymizer_PreservesMultimodalImageAndAnonymizesOnlyText()
    {
        // 多模态 content：一段含手机号的文本 + 一张图片。脱敏必须保留 image_url 结构，仅替换文本里的 PII。
        var content = JsonDocument.Parse(
            """
            [
              {"type":"text","text":"Reach me at 13812345678."},
              {"type":"image_url","image_url":{"url":"https://example.test/img.png","detail":"high"}}
            ]
            """).RootElement.Clone();

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = content }
            }
        };

        var (sanitized, piiMap, containsPii) = PiiAnonymizer.AnonymizeRequest(request);

        Assert.True(containsPii);
        var sanitizedContent = sanitized.Messages![0].Content!.Value;
        Assert.Equal(JsonValueKind.Array, sanitizedContent.ValueKind);

        string? textPart = null;
        string? imageUrl = null;
        string? imageDetail = null;
        foreach (var item in sanitizedContent.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("type", out var typeEl));
            switch (typeEl.GetString())
            {
                case "text":
                    textPart = item.GetProperty("text").GetString();
                    break;
                case "image_url":
                    var img = item.GetProperty("image_url");
                    imageUrl = img.GetProperty("url").GetString();
                    imageDetail = img.GetProperty("detail").GetString();
                    break;
            }
        }

        // 文本片段：手机号被占位符替换，可还原。
        Assert.NotNull(textPart);
        Assert.Contains("[PII_PHONE_1]", textPart);
        Assert.DoesNotContain("13812345678", textPart);
        Assert.Contains("13812345678", PiiAnonymizer.DeanonymizeText(textPart!, piiMap));

        // 图片部分：结构完整保留（这正是回归所保护的——此前会被 FromText 压平丢弃）。
        Assert.Equal("https://example.test/img.png", imageUrl);
        Assert.Equal("high", imageDetail);
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
