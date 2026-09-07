using System.Text.Json;
using OptiRouter.Mcp;
using Xunit;

namespace OptiRouter.Tests.Mcp;

/// <summary>
/// McpServerHost 协议层单测：JSON-RPC 2.0 错误形状（error 字段而非包进 result）、
/// 工具调用超时兜底、工具失败语义（isError 内容而非协议 error）。
/// </summary>
public sealed class McpServerHostTests
{
    private sealed class StubToolProvider : McpServerToolProvider
    {
        public Func<string, JsonElement?, CancellationToken, Task<(bool, string?, string?)>>? OnExecute { get; set; }

        public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpServerTool>>(new[]
            {
                new McpServerTool("echo", "echo tool",
                    JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }))
            });

        public Task<(bool Success, string? Content, string? Error)> ExecuteToolAsync(
            string toolName, JsonElement? arguments, CancellationToken ct = default)
            => OnExecute?.Invoke(toolName, arguments, ct) ?? Task.FromResult<(bool, string?, string?)>((true, "ok", null));
    }

    private static McpServerHost CreateHost(StubToolProvider provider, int timeoutMs = 30_000) =>
        new(new McpServerOptions { Enabled = true, Path = "/mcp", MaxToolCallTimeoutMs = timeoutMs }, provider);

    private static async Task<JsonDocument> CallAsync(McpServerHost host, string body)
    {
        var response = await host.HandleRequestAsync(body);
        Assert.Equal(200, response.StatusCode);
        return JsonDocument.Parse(response.Body);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcErrorShape()
    {
        var host = CreateHost(new StubToolProvider());

        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","method":"resources/list","id":7}""");

        var root = doc.RootElement;
        // 修复前：code/message 被包进 result 字段——严格客户端会把协议错误当成功结果
        Assert.False(root.TryGetProperty("result", out _));
        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("resources/list", root.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_MissingName_Returns32602Error()
    {
        var host = CreateHost(new StubToolProvider());

        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","method":"tools/call","params":{"arguments":{}},"id":1}""");

        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("result", out _));
        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_Success_ReturnsContentResult()
    {
        var provider = new StubToolProvider
        {
            OnExecute = (name, args, ct) => Task.FromResult<(bool, string?, string?)>((true, $"echo:{name}", null))
        };
        var host = CreateHost(provider);

        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo"},"id":2}""");

        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("echo:echo", root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_FailingTool_ReturnsIsErrorContent_NotProtocolError()
    {
        // 工具自身执行失败属 MCP isError 内容语义，不是 JSON-RPC 协议错误
        var provider = new StubToolProvider
        {
            OnExecute = (name, args, ct) => Task.FromResult<(bool, string?, string?)>((false, null, "boom"))
        };
        var host = CreateHost(provider);

        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo"},"id":3}""");

        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("boom", root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_UnresponsiveTool_TimesOutWithIsError()
    {
        // 不响应取消 token 的挂死工具：MaxToolCallTimeoutMs 到期后经 WaitAsync 竞速返回超时错误
        var provider = new StubToolProvider
        {
            OnExecute = async (name, args, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
                return (true, "late", (string?)null);
            }
        };
        var host = CreateHost(provider, timeoutMs: 1_000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo"},"id":4}""");
        sw.Stop();

        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _));
        Assert.True(root.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("timed out", root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"Expected timeout around 1s, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ToolsCall_MissingMethod_Returns32601()
    {
        var host = CreateHost(new StubToolProvider());

        using var doc = await CallAsync(host,
            """{"jsonrpc":"2.0","id":5}""");

        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
