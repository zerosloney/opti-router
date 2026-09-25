using System.Net;
using System.Text;
using OptiRouter.Clients;
using Xunit;

namespace OptiRouter.Tests.Protocols;

// 注意：本目录既有约定用 Protocols 命名空间——若用 OptiRouter.Tests.Clients，
// 其他测试文件里 `Clients.ChatResponse` 之类的限定名会解析到测试命名空间而非 OptiRouter.Clients。

/// <summary>
/// 回归：SendStreamRequestAsync 错误正文读取自身超时（上游返回错误状态后正文挂住）时，
/// response 必须在重试下一轮前释放——修复前读取超时异常直接逃出，无 finally 收尾，
/// 已建连的 HttpResponseMessage 泄漏、连接不归还连接池（重试路径可反复触发）。
/// </summary>
public sealed class ModelClientRequestPipelineTests
{
    [Fact]
    public async Task ErrorBodyReadTimeout_DisposesResponse_BeforeRetry()
    {
        using var handler = new ScriptedHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        // 第 1 轮：500 + 正文挂死（错误正文读取超时，异常可重试）→ 重试；
        // 第 2 轮：429（IsRetryable=false）→ 抛 ModelClientException 终止。
        var ex = await Assert.ThrowsAsync<ModelClientException>(() =>
            ModelClientRequestPipeline.SendStreamRequestAsync(
                httpClient,
                () => new HttpRequestMessage(HttpMethod.Post, "chat/completions"),
                connectTimeout: TimeSpan.FromMilliseconds(300),
                maxRetries: 1,
                ct: CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(handler.FirstContentDisposedWhenSecondArrived,
            "重试第 2 轮开始时，第 1 轮超时的错误响应仍未释放（泄漏回归）。");
        Assert.True(handler.FirstContent!.ContentDisposed);
    }

    /// <summary>第 1 次调用返回 500 + 挂死正文；之后返回 429 + 可读正文。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public TrackingStreamContent? FirstContent { get; private set; }
        public bool FirstContentDisposedWhenSecondArrived { get; private set; }
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                FirstContent = new TrackingStreamContent(new HangingStream());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = FirstContent
                });
            }

            FirstContentDisposedWhenSecondArrived = FirstContent!.ContentDisposed;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("quota exhausted", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TrackingStreamContent(Stream stream) : StreamContent(stream)
    {
        public bool ContentDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            ContentDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>读取端取消才会返回的流（模拟错误正文悬挂的上游）。</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
