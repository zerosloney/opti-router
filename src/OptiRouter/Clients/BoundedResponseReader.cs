using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace OptiRouter.Clients;

/// <summary>
/// 有界响应读取工具：三种协议客户端共用的 OOM 防护。
/// 非流式响应体与流式单行都有字节上限，恶意/异常上游的超大响应在越限时立即中断
/// （抛 <see cref="ResponseSizeLimitExceededException"/>），而非全量进内存。
/// </summary>
internal static class BoundedResponseReader
{
    /// <summary>非流式响应体上限（1 MB）。</summary>
    public const int MaxNonStreamingResponseBytes = 1024 * 1024;

    /// <summary>流式单行上限（1 MB），与 OpenAI 客户端 PipeReader 实现一致。</summary>
    public const int MaxStreamLineBytes = 1024 * 1024;

    /// <summary>
    /// 在完整物化前读取有限大小的 UTF-8 响应体。
    /// </summary>
    public static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength is > MaxNonStreamingResponseBytes)
        {
            throw new ResponseSizeLimitExceededException(MaxNonStreamingResponseBytes,
                $"Upstream response body exceeded {MaxNonStreamingResponseBytes} bytes; aborting to prevent OOM.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        int initialCapacity = content.Headers.ContentLength is > 0
            ? (int)content.Headers.ContentLength.Value
            : 0;
        using var body = new MemoryStream(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            while (true)
            {
                long remaining = MaxNonStreamingResponseBytes - body.Length;
                int readLength = (int)Math.Min(buffer.Length, remaining + 1);
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (body.Length + bytesRead > MaxNonStreamingResponseBytes)
                {
                    throw new ResponseSizeLimitExceededException(MaxNonStreamingResponseBytes,
                        $"Upstream response body exceeded {MaxNonStreamingResponseBytes} bytes; aborting to prevent OOM.");
                }

                body.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 逐行读取流（含 SSE 行），单行超过 <see cref="MaxStreamLineBytes"/> 立即中断。
    /// 行尾 \r\n 与 \n 均归一为不含 \r 的行。替代 StreamReader.ReadLineAsync 的无行长限制读取。
    /// </summary>
    /// <param name="stream">待读取的响应流。</param>
    /// <param name="idleTimeout">
    /// 空闲超时：相邻读取间隔超过该值（无任何新字节）抛 TaskCanceledException(inner: TimeoutException)。
    /// null 时不限。流式无总时长上限——持续推进的流可跑任意久。
    /// </param>
    /// <param name="cancellationToken">外部取消令牌（客户端断开等）。</param>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        TimeSpan? idleTimeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[8 * 1024];
        var line = new List<byte>(256);
        CancellationTokenSource? idleCts = null;
        try
        {
            while (true)
            {
                int bytesRead;
                if (idleTimeout is { } idle)
                {
                    // 每轮读取前重置计时：有数据推进就续命，只有真死流才超时。
                    CancellationToken readToken = ModelClientRetry.IdleReadToken(ref idleCts, idle, cancellationToken);
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, readToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (idleCts is not null && ModelClientRetry.IsIdleTimeout(idleCts, cancellationToken))
                    {
                        throw ModelClientRetry.IdleTimeoutException(idle);
                    }
                }
                else
                {
                    bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                if (bytesRead == 0) break;

                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        yield return TakeLine(line);
                    }
                    else
                    {
                        line.Add(buffer[i]);
                        if (line.Count > MaxStreamLineBytes)
                        {
                            throw new ResponseSizeLimitExceededException(MaxStreamLineBytes,
                                $"Upstream stream line exceeded {MaxStreamLineBytes} bytes; aborting to prevent OOM.");
                        }
                    }
                }
            }
        }
        finally
        {
            idleCts?.Dispose();
        }

        if (line.Count > 0)
        {
            yield return TakeLine(line);
        }
    }

    private static string TakeLine(List<byte> line)
    {
        int length = line.Count;
        if (length > 0 && line[length - 1] == (byte)'\r')
        {
            length--;
        }
        string result = Encoding.UTF8.GetString(line.ToArray(), 0, length);
        line.Clear();
        return result;
    }
}
