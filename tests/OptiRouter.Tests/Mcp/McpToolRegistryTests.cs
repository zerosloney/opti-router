using System.Text.Json;
using OptiRouter.Mcp;
using Xunit;

namespace OptiRouter.Tests.Mcp;

public class McpToolRegistryTests
{
    [Fact]
    public void Registry_RegisterAndQueryTools_WorksAccurately()
    {
        var registry = new McpToolRegistry();

        var server = new McpServerRegistration
        {
            Name = "github-server",
            BaseUrl = "http://localhost:8080",
            Enabled = true
        };
        registry.RegisterServer(server);

        var tool = new McpToolRegistration
        {
            Name = "create_pull_request",
            ServerName = "github-server",
            Description = "Create a GitHub PR",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}}}").RootElement
        };
        registry.RegisterTool(tool);

        var retrieved = registry.GetTool("create_pull_request");
        Assert.NotNull(retrieved);
        Assert.Equal("github-server", retrieved.ServerName);
        Assert.Single(registry.GetAllTools());

        var openAiTools = registry.ExportOpenAiTools();
        Assert.Equal(JsonValueKind.Array, openAiTools.ValueKind);
        Assert.Equal(1, openAiTools.GetArrayLength());
    }

    [Fact]
    public void Registry_HealthStats_TracksLatencyAndDegradation()
    {
        var registry = new McpToolRegistry();

        registry.RecordToolExecution("db_query", success: true, latencyMs: 120);
        registry.RecordToolExecution("db_query", success: true, latencyMs: 80);

        var stats = registry.GetToolHealth("db_query");
        Assert.Equal(2, stats.TotalCalls);
        Assert.Equal(0, stats.FailedCalls);
        Assert.Equal(100.0, stats.AverageLatencyMs);
        Assert.False(stats.IsDegraded);

        // Record 5 failures
        for (int i = 0; i < 5; i++)
        {
            registry.RecordToolExecution("db_query", success: false, latencyMs: 5000);
        }

        Assert.Equal(7, stats.TotalCalls);
        Assert.Equal(5, stats.FailedCalls);
        Assert.True(stats.FailureRate > 0.5);
        Assert.True(stats.IsDegraded);
    }
}
