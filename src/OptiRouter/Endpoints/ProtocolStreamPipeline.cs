using System.Text;
using Microsoft.AspNetCore.Http;
using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>
/// 协议入口（Anthropic / Gemini / OpenAI Responses）共用的流式外壳：
/// 首帧探活（区分"空流"与"首帧前即故障"）、帧写出、枚举器释放单源，
/// 协议差异留在三个回调里（data 行翻译、空流收尾载荷、故障错误帧）。
/// <para>
/// <see cref="ChatCompletionsEndpoint"/> 不走这里：它是透明透传，自己合成缺失的
/// finish_reason 分片，且首帧前的上游拒绝直接落 HTTP 错误而非 SSE 流，语义与翻译型入口不同。
/// </para>
/// </summary>
internal static class ProtocolStreamPipeline
{
    /// <summary>
    /// 流内上游故障集合（断流 502 检测 / 流内 error / 连接失败）：
    /// 各端点 FailureFrame 据此决定是否外发真实原因；是否区分首帧前、用什么词汇外发留在端点。
    /// </summary>
    internal static bool IsUpstreamStreamFailure(Exception ex)
        => ex is ModelClientException or HttpRequestException or IOException;

    /// <summary>
    /// 跑完一条协议流式响应。
    /// </summary>
    /// <param name="orchestrator">路由编排器（提供流式候选执行）。</param>
    /// <param name="request">已翻译为内部契约的请求。</param>
    /// <param name="sessionId">会话标识，null 表示无会话。</param>
    /// <param name="emptyStreamPayload">空流收尾载荷（各协议完整终结事件序列），由全新翻译器对 <c>[DONE]</c> 生成。</param>
    /// <param name="translate">内部 SSE data 行 → 协议 SSE 帧序列；同一翻译器实例贯穿整条流（有状态）。</param>
    /// <param name="failureFrame">
    /// 异常 → 该协议的 in-band 错误帧。<c>beforeFirstEvent</c> 标记故障发生在首帧之前还是流中——
    /// 各协议对这两类故障的可重试口径不同，分类留在端点。
    /// </param>
    /// <param name="ct">请求中止令牌。</param>
    internal static async Task<IResult> RunAsync(
        ProxyOrchestrator orchestrator,
        ChatRequest request,
        string? sessionId,
        Func<string> emptyStreamPayload,
        Func<string, IReadOnlyList<string>> translate,
        Func<Exception, bool, string> failureFrame,
        CancellationToken ct)
    {
        IAsyncEnumerator<RawStreamLine>? enumerator = null;
        string? firstData = null;
        try
        {
            enumerator = orchestrator.StreamAsync(request, ct, sessionId).GetAsyncEnumerator(ct);
            if (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                firstData = enumerator.Current.Data;
            }
        }
        catch (Exception ex)
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            return SinglePayloadStream(failureFrame(ex, true), ct);
        }

        if (firstData is null)
        {
            await enumerator!.DisposeAsync().ConfigureAwait(false);
            return SinglePayloadStream(emptyStreamPayload(), ct);
        }

        var streamEnumerator = enumerator!;
        return Results.Stream(async stream =>
        {
            try
            {
                await WriteFramesAsync(stream, translate(firstData), ct).ConfigureAwait(false);
                while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    await WriteFramesAsync(stream, translate(streamEnumerator.Current.Data), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(failureFrame(ex, false)), ct).ConfigureAwait(false);
            }
            finally
            {
                await streamEnumerator.DisposeAsync().ConfigureAwait(false);
            }
        }, "text/event-stream");
    }

    private static IResult SinglePayloadStream(string payload, CancellationToken ct)
        => Results.Stream(
            stream => stream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct).AsTask(),
            "text/event-stream");

    private static async Task WriteFramesAsync(Stream stream, IReadOnlyList<string> frames, CancellationToken ct)
    {
        foreach (string frame in frames)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame), ct).ConfigureAwait(false);
        }
    }
}
