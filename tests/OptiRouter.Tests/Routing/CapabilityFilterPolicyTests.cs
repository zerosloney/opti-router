using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class CapabilityFilterPolicyTests
{
    private static RouterContext MakeContext(RouterOptions options, ChatRequest request)
    {
        return new RouterContext
        {
            Request = request,
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };
    }

    private static RouterDecision MakeInitial(IEnumerable<ModelEndpointOptions> candidates) => new()
    {
        Candidates = candidates.ToList(),
        Reason = "initial",
        EstimatedInputTokens = 0
    };

    private static ModelEndpointOptions ModelWithTags(string name, params string[] tags)
    {
        var m = new ModelEndpointOptions
        {
            Name = name,
            Tier = ModelTier.Medium,
            MaxContextTokens = 8000
        };
        foreach (var t in tags) m.Tags.Add(t);
        return m;
    }

    [Fact]
    public void Apply_Disabled_PassesThrough()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = false;
        var policy = new CapabilityFilterPolicy();
        var request = TestHelpers.BuildRequest(("user", "hi"));

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Contains("capability-filter: disabled", result.Reason);
    }

    [Fact]
    public void Apply_NoRequirements_PassesThrough()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("m1"));
        var policy = new CapabilityFilterPolicy();
        var request = TestHelpers.BuildRequest(("user", "just text"));

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Contains("capability-filter: no-requirements", result.Reason);
    }

    [Fact]
    public void Apply_VisionRequest_FiltersNonVisionModels()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("vision-model", "vision"));
        options.Models.Add(ModelWithTags("text-only"));
        var policy = new CapabilityFilterPolicy();
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(new object[]
                    {
                        new { type = "text", text = "describe this" },
                        new { type = "image_url", image_url = new { url = "http://x/y.png" } }
                    })
                }
            }
        };

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Single(result.Candidates);
        Assert.Equal("vision-model", result.Candidates[0].Name);
    }

    [Fact]
    public void Apply_ToolUseRequest_FiltersNonToolModels()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("tool-model", "tool-use"));
        options.Models.Add(ModelWithTags("plain"));
        var policy = new CapabilityFilterPolicy();

        var extData = new Dictionary<string, JsonElement>
        {
            ["tools"] = JsonSerializer.SerializeToElement(new[]
            {
                new { type = "function", function = new { name = "get_weather" } }
            })
        };
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "what's the weather?") },
            ExtensionData = extData
        };

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Single(result.Candidates);
        Assert.Equal("tool-model", result.Candidates[0].Name);
    }

    [Fact]
    public void Apply_EmptyToolsArray_NotTreatedAsToolUse()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("plain"));
        var policy = new CapabilityFilterPolicy();

        var extData = new Dictionary<string, JsonElement>
        {
            ["tools"] = JsonSerializer.SerializeToElement(Array.Empty<object>())
        };
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
            ExtensionData = extData
        };

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Contains("capability-filter: no-requirements", result.Reason);
    }

    [Fact]
    public void Apply_JsonModeRequest_FiltersNonJsonModels()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("json-model", "json-mode"));
        options.Models.Add(ModelWithTags("plain"));
        var policy = new CapabilityFilterPolicy();

        var extData = new Dictionary<string, JsonElement>
        {
            ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" })
        };
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "give me json") },
            ExtensionData = extData
        };

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Single(result.Candidates);
        Assert.Equal("json-model", result.Candidates[0].Name);
    }

    [Fact]
    public void Apply_NoModelMatches_FailsClosedWithExplicitReason()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("plain1"));
        options.Models.Add(ModelWithTags("plain2"));
        var policy = new CapabilityFilterPolicy();
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(new object[]
                    {
                        new { type = "image_url", image_url = new { url = "http://x/y.png" } }
                    })
                }
            }
        };

        var ctx = MakeContext(options, request);
        var initial = MakeInitial(options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Empty(result.Candidates);
        Assert.Contains("capability-filter: required vision; no eligible candidate supports all required capabilities", result.Reason);
    }

    [Fact]
    public void Apply_AllCandidatesMatch_ReportsNoRemoval()
    {
        var options = new RouterOptions();
        options.Routing.EnableCapabilityFilter = true;
        options.Models.Add(ModelWithTags("m1", "vision"));
        options.Models.Add(ModelWithTags("m2", "vision"));
        var policy = new CapabilityFilterPolicy();
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(new object[]
                    {
                        new { type = "image_url", image_url = new { url = "http://x/y.png" } }
                    })
                }
            }
        };

        var ctx = MakeContext(options, request);
        var result = policy.Apply(ctx, MakeInitial(options.Models));

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("capability-filter: all candidates match", result.Reason);
    }
}
