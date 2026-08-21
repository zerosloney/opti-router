using System.Net;
using System.Text;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Protocols;

/// <summary>
/// 流式/非流式超时语义测试（方案 B）：
/// - 非流式与流式建连阶段：TimeoutSeconds = 总时长上限；
/// - 流式响应体：TimeoutSeconds = 相邻 chunk 空闲上限，无总时长上限——持续推进的长流不被切断；
/// - 内部超时抛 TaskCanceledException(inner: TimeoutException)，与 HttpClient.Timeout 原生签名一致，
///   ModelClientRetry 的可重试判定与上游故障分类零改动。
/// </summary>
public sealed class ModelClientTimeoutTests
{
    private static ModelEndpointOptions Endpoint(int timeoutSeconds, int maxRetries = 0) => new()
    {
        Name = "timeout-test",
        Id = "timeout-test",
        BaseUrl = "http://localhost/",
        ApiKey = "sk-test",
        Protocol = ProviderProtocol.OpenAI,
        Enabled = true,
        TimeoutSeconds = timeoutSeconds,
        MaxRetries = maxRetries
    };

    private static ChatRequest Request() => new()
    {
        Model = "timeout-test",
        Messages = new List<ChatMessage> { ChatMessage.FromText("user", "ping") }
    };

    private static OpenAICompatibleModelClient CreateClient(HttpMessageHandler handler, ModelEndpointOptions endpoint)
        => new(endpoint, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

    [Fact]
    public async Task NonStreaming_TotalTimeout_ThrowsHttpClientTimeoutSignature()
    {
        using var handler = new HangingHandler();
        var client = CreateClient(handler, Endpoint(timeoutSeconds: 1));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteRawAsync(Request(), CancellationToken.None));

        sw.Stop();
        var tce = Assert.IsType<TaskCanceledException>(ex);
        Assert.IsType<TimeoutException>(tce.InnerException); // 可重试判定依赖此签名
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"timeout took {sw.Elapsed}");
    }

    [Fact]
    public async Task Streaming_IdleTimeout_AbortsDeadStreamAfterFirstChunk()
    {
        // 先发一条正常 SSE 数据行，然后挂死：空闲超时（1s）应断流，
        // 且已下发的首条数据照常产出。
        var sse = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n");
        using var handler = new SseHandler((sse, TimeSpan.Zero)); // 之后挂死
        var client = CreateClient(handler, Endpoint(timeoutSeconds: 1));

        var lines = new List<RawStreamLine>();
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await foreach (var line in client.StreamRawAsync(Request(), CancellationToken.None))
                lines.Add(line);
        });

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Single(lines); // 首条数据已收到
        Assert.Contains("hi", lines[0].Data);
    }

    [Fact]
    public async Task Streaming_ProgressingStream_NotCutByTotalDuration()
    {
        // 5 条数据行、每条间隔 300ms：总时长 1.5s > TimeoutSeconds(1s)，但相邻间隔 300ms < 空闲上限 1s。
        // 旧语义（HttpClient.Timeout 总时长）会在 1s 处腰斩；新空闲语义应完整收完并以 [DONE] 结束。
        string line = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n";
        using var handler = new SseHandler(
            (Encoding.UTF8.GetBytes(line), TimeSpan.Zero),
            (Encoding.UTF8.GetBytes(line), TimeSpan.FromMilliseconds(300)),
            (Encoding.UTF8.GetBytes(line), TimeSpan.FromMilliseconds(300)),
            (Encoding.UTF8.GetBytes(line), TimeSpan.FromMilliseconds(300)),
            (Encoding.UTF8.GetBytes(line), TimeSpan.FromMilliseconds(300)),
            (Encoding.UTF8.GetBytes("data: [DONE]\n\n"), TimeSpan.FromMilliseconds(300)));
        var client = CreateClient(handler, Endpoint(timeoutSeconds: 1));

        var lines = new List<RawStreamLine>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var line2 in client.StreamRawAsync(Request(), CancellationToken.None))
            lines.Add(line2);
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.2), $"stream finished too early: {sw.Elapsed}");
        Assert.Equal(6, lines.Count); // 5 条数据 + [DONE]
        Assert.Equal("[DONE]", lines[^1].Data);
    }

    /// <summary>永不完成的 handler（非流式总超时用）。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// 按脚本吐 SSE 的 handler：每个 chunk 在指定延迟后可读；脚本耗尽后挂死
    /// （读取端取消时 Task.Delay 抛 OCE，与真实连接中止一致）。
    /// </summary>
    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly ScriptedStream _stream;

        public SseHandler(params (byte[] Chunk, TimeSpan DelayBefore)[] script)
            => _stream = new ScriptedStream(script);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(_stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly List<(byte[] Chunk, TimeSpan DelayBefore)> _script;
        private int _index;
        private int _offset;

        public ScriptedStream(IEnumerable<(byte[] Chunk, TimeSpan DelayBefore)> script)
            => _script = new List<(byte[], TimeSpan)>(script);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _script.Count)
            {
                // 脚本耗尽：挂死直到读取端取消（模拟上游停止发送）。
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return 0;
            }

            var (chunk, delay) = _script[_index];
            if (_offset == 0 && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            int n = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            if (_offset == chunk.Length)
            {
                _index++;
                _offset = 0;
            }
            return n;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
