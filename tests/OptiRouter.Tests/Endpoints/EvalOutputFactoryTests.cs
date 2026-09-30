using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;

namespace OptiRouter.Tests.Endpoints;

public sealed class EvalOutputFactoryTests
{
    private static ModelEndpointOptions Model(string name, string id, decimal input = 1.0m, decimal output = 2.0m)
        => new() { Name = name, Id = id, InputPricePerMillion = input, OutputPricePerMillion = output };

    private static RawChatResponse Response(string modelField, ChatUsage? usage)
        => new($$"""{ "model": "{{modelField}}", "choices": [] }""", usage);

    [Fact]
    public void ResolvesCandidateByUpstreamId_AndComputesCostFromUsage()
    {
        var models = new[] { Model("deepseek/deepseek-chat", "deepseek-chat", input: 0.14m, output: 0.28m) };
        var usage = new ChatUsage { PromptTokens = 1000, CompletionTokens = 2000, TotalTokens = 3000 };

        var output = EvalOutputFactory.FromRawResponse(Response("deepseek-chat", usage), models);

        // 成本必须等于定价计算器口径（本批接线修复的关键断言），且非零可区分模型价差
        Assert.Equal(CostCalculator.Compute(usage, models[0]), output.Cost);
        Assert.True(output.Cost > 0m);
        Assert.Equal("deepseek/deepseek-chat", output.SelectedModel);
    }

    [Fact]
    public void ResolvesCandidateByName_WhenBodyModelMatchesDisplayName()
    {
        var models = new[] { Model("mock-strong", "mock-strong") };
        var usage = new ChatUsage { PromptTokens = 10, CompletionTokens = 10, TotalTokens = 20 };

        var output = EvalOutputFactory.FromRawResponse(Response("mock-strong", usage), models);

        Assert.Equal("mock-strong", output.SelectedModel);
        Assert.True(output.Cost > 0m);
    }

    [Fact]
    public void UnknownUpstreamModel_YieldsZeroCostAndFallsBackToBodyExtraction()
    {
        var models = new[] { Model("known", "known") };

        var output = EvalOutputFactory.FromRawResponse(Response("not-configured", new ChatUsage { PromptTokens = 10, CompletionTokens = 10, TotalTokens = 20 }), models);

        Assert.Equal(0m, output.Cost);
        Assert.Null(output.SelectedModel);
    }

    [Fact]
    public void MissingUsage_YieldsZeroCost_ButStillResolvesModel()
    {
        var models = new[] { Model("known", "known") };

        var output = EvalOutputFactory.FromRawResponse(Response("known", usage: null), models);

        Assert.Equal(0m, output.Cost);
        Assert.Equal("known", output.SelectedModel);
    }

    [Fact]
    public void MalformedBody_DoesNotThrow_TreatedAsUnknownModel()
    {
        var models = new[] { Model("known", "known") };
        var response = new RawChatResponse("not-json{", new ChatUsage { PromptTokens = 10, CompletionTokens = 10, TotalTokens = 20 });

        var output = EvalOutputFactory.FromRawResponse(response, models);

        Assert.Equal(0m, output.Cost);
        Assert.Null(output.SelectedModel);
    }
}
