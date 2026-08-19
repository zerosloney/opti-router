using System.Text.Json;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Mcp;
using OptiRouter.Tests.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Mcp;

public sealed class McpToolOrchestratorTests
{
    private static ModelEndpointOptions CreateEndpoint(string name = "model-a")
    {
        return new ModelEndpointOptions
        {
            Name = name,
            BaseUrl = "https://api.example.com",
            ApiKey = "sk-test",
            Tier = ModelTier.Medium,
            MaxContextTokens = 8192,
            Enabled = true
        };
    }

    private static McpToolRegistry CreateRegistry()
    {
        var registry = new McpToolRegistry();
        registry.RegisterServer(new McpServerRegistration
        {
            Name = "weather-srv",
            BaseUrl = "http://localhost:3001/mcp",
            Enabled = true
        });
        registry.RegisterTool(new McpToolRegistration
        {
            Name = "get_weather",
            ServerName = "weather-srv",
            Description = "Get weather"
        });
        return registry;
    }

    private sealed class FakeToolExecutor : IMcpToolExecutor
    {
        private readonly Func<string, JsonElement, McpToolCallResult> _handler;
        public List<(string Tool, JsonElement Arguments)> Calls { get; } = new();

        public FakeToolExecutor(Func<string, JsonElement, McpToolCallResult>? handler = null)
        {
            _handler = handler ?? ((name, args) => new McpToolCallResult(true, $"result-of-{name}", null));
        }

        public Task<McpToolCallResult> ExecuteToolAsync(McpServerRegistration server, string toolName, JsonElement arguments, CancellationToken ct = default)
        {
            Calls.Add((toolName, arguments.Clone()));
            return Task.FromResult(_handler(toolName, arguments));
        }
    }

    private const string ToolCallResponse = """
        {"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":null,
        "tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Beijing\"}"}}]}}],
        "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
        """;

    private const string FinalResponse = """
        {"id":"chatcmpl-2","choices":[{"message":{"role":"assistant","content":"Beijing weather is sunny, 24C."}}],
        "usage":{"prompt_tokens":30,"completion_tokens":8,"total_tokens":38}}
        """;

    private const string MalformedAssistantToolCallResponse = """
        {"id":"chatcmpl-bad","choices":[{"message":{"role":123,"content":null,
        "tool_calls":[{"id":"call_bad","type":"function","function":{"name":"get_weather","arguments":"{}"}}]}}],
        "usage":{"prompt_tokens":11,"completion_tokens":5,"total_tokens":16}}
        """;

    private static RawChatResponse ToRaw(string json) => new(json, null);

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_RecordsSupersededRoundCosts_WithoutDoubleCountingFinal()
    {
        // C2 成本入账：入口响应与中间轮响应被重放取代时逐轮入账（此前全部漏计导致预算低估），
        // 最终返回响应留给调用方计费，不重复入账。
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        var endpoint = CreateEndpoint();
        endpoint.InputPricePerMillion = 2m;
        endpoint.OutputPricePerMillion = 4m;

        var ledger = new OptiRouter.Routing.CostLedger();
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: ledger,
            options: new FakeOptionsMonitor(new RouterOptions()),
            affinityCache: new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            tsStore: new OptiRouter.Routing.ThompsonStateStore(),
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<OutcomeRecorder>.Instance);

        var usageEntry = new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
        var usageMid = new ChatUsage { PromptTokens = 20, CompletionTokens = 6, TotalTokens = 26 };
        var usageFinal = new ChatUsage { PromptTokens = 30, CompletionTokens = 8, TotalTokens = 38 };
        int upstreamCalls = 0;
        var secondToolCallResponse = ToolCallResponse.Replace("chatcmpl-1", "chatcmpl-2").Replace("call_1", "call_2").Replace("Beijing", "Shanghai");
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            upstreamCalls++;
            return Task.FromResult(new RawChatResponse(
                upstreamCalls == 1 ? secondToolCallResponse : FinalResponse,
                upstreamCalls == 1 ? usageMid : usageFinal));
        });
        var orchestrator = new McpToolOrchestrator(registry, executor,
            new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }), recorder: recorder);

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Check weather twice") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(
            request, new RawChatResponse(ToolCallResponse, usageEntry), endpoint, maxRounds: 4, sessionId: "s-mcp");

        // 两轮工具执行：入口响应（usageEntry）+ 第一轮重放响应（usageMid）被取代入账；最终响应（usageFinal）不入账
        Assert.Equal(2, upstreamCalls);
        decimal expected = CostCalculator.Compute(usageEntry, endpoint) + CostCalculator.Compute(usageMid, endpoint);
        Assert.Equal(expected, ledger.GetSpend().Total);
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_DuplicateSignature_DoesNotChargeUnreplacedResponse()
    {
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        var endpoint = CreateEndpoint();
        endpoint.InputPricePerMillion = 2m;
        endpoint.OutputPricePerMillion = 4m;
        var (recorder, ledger) = CreateCostRecorder();

        var entryUsage = new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
        var duplicateUsage = new ChatUsage { PromptTokens = 20, CompletionTokens = 6, TotalTokens = 26 };
        int upstreamCalls = 0;
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            upstreamCalls++;
            return Task.FromResult(new RawChatResponse(ToolCallResponse, duplicateUsage));
        });
        var orchestrator = new McpToolOrchestrator(registry, executor,
            new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }), recorder: recorder);

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Loop") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(
            request, new RawChatResponse(ToolCallResponse, entryUsage), endpoint, maxRounds: 4, sessionId: "s-duplicate");

        Assert.Single(executor.Calls);
        Assert.Equal(1, upstreamCalls);
        Assert.Equal(ToolCallResponse, result.Body);
        Assert.Equal(CostCalculator.Compute(entryUsage, endpoint), ledger.GetSpend().Total);
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_MalformedAssistant_DoesNotChargeUnreplayedResponse()
    {
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        var endpoint = CreateEndpoint();
        endpoint.InputPricePerMillion = 2m;
        endpoint.OutputPricePerMillion = 4m;
        var (recorder, ledger) = CreateCostRecorder();
        var usage = new ChatUsage { PromptTokens = 11, CompletionTokens = 5, TotalTokens = 16 };
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            throw new InvalidOperationException("replay should not run");
        });
        var orchestrator = new McpToolOrchestrator(registry, executor,
            new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }), recorder: recorder);

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Malformed") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(
            request, new RawChatResponse(MalformedAssistantToolCallResponse, usage), endpoint, maxRounds: 4);

        Assert.Equal(MalformedAssistantToolCallResponse, result.Body);
        Assert.Empty(executor.Calls);
        Assert.Equal(0m, ledger.GetSpend().Total);
    }

    private sealed class FakeOptionsMonitor(RouterOptions current) : IOptionsMonitor<RouterOptions>
    {
        public RouterOptions CurrentValue => current;
        public RouterOptions Get(string? name) => current;
        public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
    }

    private static (OutcomeRecorder Recorder, OptiRouter.Routing.CostLedger Ledger) CreateCostRecorder()
    {
        var ledger = new OptiRouter.Routing.CostLedger();
        var recorder = new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: ledger,
            options: new FakeOptionsMonitor(new RouterOptions()),
            affinityCache: new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            tsStore: new OptiRouter.Routing.ThompsonStateStore(),
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<OutcomeRecorder>.Instance);
        return (recorder, ledger);
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_ExecutesToolAndReplaysWithToolMessages()
    {
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        int upstreamCalls = 0;
        var receivedRequests = new List<ChatRequest>();
        var endpoint = CreateEndpoint();
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            receivedRequests.Add(req);
            upstreamCalls++;
            return Task.FromResult(ToRaw(FinalResponse)); // 重放后模型直接给出最终答案
        });
        var orchestrator = new McpToolOrchestrator(registry, executor, new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }));

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "What is the weather in Beijing?") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(request, ToRaw(ToolCallResponse), endpoint, maxRounds: 4);

        // 工具被精确执行一次
        var call = Assert.Single(executor.Calls);
        Assert.Equal("get_weather", call.Tool);
        Assert.Equal("Beijing", call.Arguments.GetProperty("city").GetString());

        // 入口响应直接传入，上游仅被重放一次
        Assert.Equal(1, upstreamCalls);
        Assert.Equal(FinalResponse, result.Body);

        // 重放请求的消息结构：原始消息 + assistant(tool_calls) + tool(tool_call_id/content)
        var replayedMessages = receivedRequests[0].Messages;
        Assert.Equal(3, replayedMessages.Count);
        Assert.Equal("user", replayedMessages[0].Role);

        Assert.Equal("assistant", replayedMessages[1].Role);
        Assert.NotNull(replayedMessages[1].ExtensionData);
        Assert.True(replayedMessages[1].ExtensionData!.ContainsKey("tool_calls"));

        Assert.Equal("tool", replayedMessages[2].Role);
        Assert.Equal("call_1", replayedMessages[2].ExtensionData!["tool_call_id"].GetString());
        Assert.Equal("result-of-get_weather", replayedMessages[2].GetText());
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_MultipleRounds_UntilNoToolCalls()
    {
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        int upstreamCalls = 0;
        var receivedRequests = new List<ChatRequest>();
        var endpoint = CreateEndpoint();
        // 第二轮调用同工具但不同参数（city=Shanghai）：签名不同才构成有效多轮推进（同签名重复会被无进展检测终止）
        var secondToolCallResponse = ToolCallResponse.Replace("chatcmpl-1", "chatcmpl-2").Replace("call_1", "call_2").Replace("Beijing", "Shanghai");
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            receivedRequests.Add(req);
            upstreamCalls++;
            return Task.FromResult(upstreamCalls == 1 ? ToRaw(secondToolCallResponse) : ToRaw(FinalResponse));
        });
        var orchestrator = new McpToolOrchestrator(registry, executor, new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }));

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Check weather twice") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(request, ToRaw(ToolCallResponse), endpoint, maxRounds: 4);

        Assert.Equal(2, executor.Calls.Count);
        Assert.Equal(2, upstreamCalls);
        Assert.Equal(FinalResponse, result.Body);

        // 第二轮重放包含两轮工具消息（4 条新消息 + 原始 user）
        var replayedMessages = receivedRequests[1].Messages;
        Assert.Equal(1 + 4, replayedMessages.Count);
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_RoundLimit_StopsReplay()
    {
        var registry = CreateRegistry();
        var executor = new FakeToolExecutor();
        int upstreamCalls = 0;
        var receivedRequests = new List<ChatRequest>();
        var endpoint = CreateEndpoint();
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            receivedRequests.Add(req);
            upstreamCalls++;
            return Task.FromResult(ToRaw(ToolCallResponse)); // 永远请求工具
        });
        var orchestrator = new McpToolOrchestrator(registry, executor, new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }));

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Loop") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(request, ToRaw(ToolCallResponse), endpoint, maxRounds: 2);

        // C2 无进展检测：模型每轮重复完全相同的 tool_calls（同签名），
        // 首轮执行后即终止循环——不再烧满剩余轮次（旧行为为执行满 2 轮）。
        Assert.Single(executor.Calls);
        Assert.Equal(1, upstreamCalls);
        Assert.Equal(ToolCallResponse, result.Body);
    }

    [Fact]
    public async Task ExecuteToolCallsAndReplayAsync_UnregisteredTool_ReturnsErrorMessageAsToolResult()
    {
        var registry = CreateRegistry(); // 只注册了 get_weather
        var executor = new FakeToolExecutor();
        int upstreamCalls = 0;
        var receivedRequests = new List<ChatRequest>();
        var endpoint = CreateEndpoint();
        var unregisteredCallResponse = ToolCallResponse.Replace("get_weather", "unknown_tool");
        var client = new MockModelClient(endpoint, (req, ct) =>
        {
            receivedRequests.Add(req);
            upstreamCalls++;
            return Task.FromResult(ToRaw(FinalResponse));
        });
        var orchestrator = new McpToolOrchestrator(registry, executor, new TestModelClientProvider(new Dictionary<string, IModelClient> { [endpoint.Name] = client }));

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") }
        };

        var result = await orchestrator.ExecuteToolCallsAndReplayAsync(request, ToRaw(unregisteredCallResponse), endpoint, maxRounds: 4);

        Assert.Empty(executor.Calls); // 未注册工具不执行
        var toolMessage = receivedRequests[0].Messages[^1];
        Assert.Equal("tool", toolMessage.Role);
        Assert.Contains("not registered", toolMessage.GetText());
        Assert.Equal(FinalResponse, result.Body);
    }

    [Fact]
    public void ExtractToolCalls_ParsesStringAndObjectArguments()
    {
        const string body = """
            {"choices":[{"message":{"tool_calls":[
                {"id":"c1","function":{"name":"alpha","arguments":"{\"a\":1}"}},
                {"id":"c2","function":{"name":"beta","arguments":{"b":"two"}}},
                {"id":"c3","type":"function"}
            ]}}]}
            """;

        var calls = McpToolOrchestrator.ExtractToolCalls(body);

        Assert.Equal(2, calls.Count); // c3 无 function.name 被跳过
        Assert.Equal("c1", calls[0].Id);
        Assert.Equal("alpha", calls[0].Name);
        Assert.Equal(1, calls[0].Arguments.GetProperty("a").GetInt32());
        Assert.Equal("c2", calls[1].Id);
        Assert.Equal("two", calls[1].Arguments.GetProperty("b").GetString());
    }

    [Fact]
    public void ExtractToolCalls_NoToolCalls_ReturnsEmpty()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","content":"plain answer"}}]}""";
        Assert.Empty(McpToolOrchestrator.ExtractToolCalls(body));
        Assert.Empty(McpToolOrchestrator.ExtractToolCalls("not-json"));
        Assert.Empty(McpToolOrchestrator.ExtractToolCalls(""));
    }
}
