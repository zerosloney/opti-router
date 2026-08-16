using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Mcp;
using Xunit;

namespace OptiRouter.Tests.Mcp;

public class McpToolComplexityAnalyzerTests
{
    private readonly McpToolComplexityAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoTools_ReturnsNone()
    {
        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hello") }
        };

        var report = _analyzer.Analyze(req);
        Assert.Equal(McpComplexityLevel.None, report.Level);
        Assert.Equal(0, report.ToolCount);
        Assert.Equal(ModelTier.Cheap, report.RecommendedMinTier);
    }

    [Fact]
    public void Analyze_SimpleTools_ReturnsSimpleLevel()
    {
        var toolsJson = @"[
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""get_weather"",
                    ""description"": ""Get current weather"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""city"": { ""type"": ""string"" },
                            ""unit"": { ""type"": ""string"", ""enum"": [""celsius"", ""fahrenheit""] }
                        },
                        ""required"": [""city""]
                    }
                }
            }
        ]";

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "What's the weather in Tokyo?") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonDocument.Parse(toolsJson).RootElement
            }
        };

        var report = _analyzer.Analyze(req);
        Assert.Equal(1, report.ToolCount);
        Assert.Equal(McpComplexityLevel.Simple, report.Level);
        Assert.Equal(ModelTier.Cheap, report.RecommendedMinTier);
        Assert.True(report.ComplexityScore <= 4.0);
    }

    [Fact]
    public void Analyze_DeepNestedSchema_ReturnsHighComplexity()
    {
        var toolsJson = @"[
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""deploy_service"",
                    ""description"": ""Deploy microservice cluster"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""cluster"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""network"": {
                                        ""type"": ""object"",
                                        ""properties"": {
                                            ""vpcId"": { ""type"": ""string"" },
                                            ""subnets"": {
                                                ""type"": ""array"",
                                                ""items"": {
                                                    ""type"": ""object"",
                                                    ""properties"": {
                                                        ""id"": { ""type"": ""string"" },
                                                        ""cidr"": { ""type"": ""string"" }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            },
                            ""env"": { ""type"": ""string"", ""enum"": [""prod"", ""staging"", ""dev"", ""canary""] }
                        },
                        ""required"": [""cluster""]
                    }
                }
            },
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""execute_sql"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""query"": { ""type"": ""string"" } }
                    }
                }
            },
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""fetch_logs"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""service"": { ""type"": ""string"" } }
                    }
                }
            },
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""send_alert"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""severity"": { ""type"": ""string"" } }
                    }
                }
            },
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""create_backup"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""dbName"": { ""type"": ""string"" } }
                    }
                }
            },
            {
                ""type"": ""function"",
                ""function"": {
                    ""name"": ""restart_node"",
                    ""parameters"": {
                        ""type"": ""object"",
                        ""properties"": { ""nodeId"": { ""type"": ""string"" } }
                    }
                }
            }
        ]";

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Deploy the production stack") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonDocument.Parse(toolsJson).RootElement
            }
        };

        var report = _analyzer.Analyze(req);
        Assert.Equal(6, report.ToolCount);
        Assert.Equal(McpComplexityLevel.High, report.Level);
        Assert.Equal(ModelTier.Strong, report.RecommendedMinTier);
        Assert.True(report.MaxNestingDepth >= 4);
    }
}
