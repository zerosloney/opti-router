using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public static class TestHelpers
{
    public static RouterOptions BuildOptions(params (string Name, ModelTier Tier, int MaxCtx, decimal InputPrice)[] models)
    {
        var opts = new RouterOptions();
        foreach (var (name, tier, maxCtx, inputPrice) in models)
        {
            opts.Models.Add(new ModelEndpointOptions
            {
                Name = name,
                Tier = tier,
                MaxContextTokens = maxCtx,
                InputPricePerMillion = inputPrice,
                OutputPricePerMillion = inputPrice * 2,
                Enabled = true
            });
        }
        return opts;
    }

    public static ChatRequest BuildRequest(params (string Role, string Content)[] messages)
    {
        return new ChatRequest
        {
            Messages = messages.Select(m => ChatMessage.FromText(m.Role, m.Content)).ToList()
        };
    }
}
