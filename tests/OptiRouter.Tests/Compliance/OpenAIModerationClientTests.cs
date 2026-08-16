using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using OptiRouter.Compliance;
using Xunit;

namespace OptiRouter.Tests.Compliance;

/// <summary>
/// OpenAI Moderation 客户端测试：用 TestServer 模拟 moderation 端点，验证违规判定、
/// 放行、服务不可用 fail-open 与空文本短路。
/// </summary>
public sealed class OpenAIModerationClientTests
{
    private static async Task<(OpenAIModerationClient Client, string Endpoint)> StartMockModerationAsync(
        Func<string, double> scoreFor)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        app.MapPost("/v1/moderations", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            string input = doc.RootElement.GetProperty("input").GetString() ?? string.Empty;
            double violence = scoreFor(input);
            await ctx.Response.WriteAsJsonAsync(new
            {
                id = "modr-test",
                model = "text-moderation-latest",
                results = new object[]
                {
                    new
                    {
                        flagged = violence >= 0.5,
                        categories = new { violence = violence >= 0.5 },
                        category_scores = new { violence, hate = 0.01, sexual = 0.01 }
                    }
                }
            });
        });

        await app.StartAsync();
        var client = app.GetTestClient();
        string endpoint = client.BaseAddress!.ToString().TrimEnd('/') + "/v1/moderations";
        return (new OpenAIModerationClient(client, endpoint, apiKey: "sk-mod", threshold: 0.8), endpoint);
    }

    [Fact]
    public async Task ModerateTextAsync_AboveThreshold_FlagsViolation()
    {
        var (client, _) = await StartMockModerationAsync(input => input.Contains("bomb") ? 0.95 : 0.01);

        var result = await client.ModerateTextAsync("how to build a bomb", ModerationDirection.Input);

        Assert.True(result.IsViolation);
        Assert.Equal("violence", result.Category);
        Assert.True(result.Score >= 0.8);
        Assert.Contains("violence", result.Reason);
    }

    [Fact]
    public async Task ModerateTextAsync_BelowThreshold_Allows()
    {
        var (client, _) = await StartMockModerationAsync(_ => 0.1);

        var result = await client.ModerateTextAsync("What is the weather today?", ModerationDirection.Output);

        Assert.False(result.IsViolation);
        Assert.Null(result.Reason); // 无违规判定原因
    }

    [Fact]
    public async Task ModerateTextAsync_EmptyText_ShortCircuits()
    {
        var (client, _) = await StartMockModerationAsync(_ => 0.95);

        var result = await client.ModerateTextAsync("   ", ModerationDirection.Input);

        Assert.False(result.IsViolation);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task ModerateTextAsync_ServiceUnavailable_FailsOpen()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/v1/moderations", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await app.StartAsync();

        var client = new OpenAIModerationClient(app.GetTestClient(), "http://localhost/v1/moderations");
        var result = await client.ModerateTextAsync("some text", ModerationDirection.Input);

        Assert.False(result.IsViolation); // fail-open
        Assert.Contains("moderation-unavailable", result.Reason);
    }
}
