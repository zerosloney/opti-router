using System.Net.Http.Headers;
using System.Text;

namespace OptiRouter.Clients;

/// <summary>
/// 三协议客户端共享的请求管道（#4 模块化：资源所有权与时间预算单源）——
/// 流式建连阶段的发送、状态校验、错误正文限时读取与重试判定。
/// 协议差异（请求体构建、事件翻译）留在各客户端，本管道不抹平。
/// </summary>
internal static class ModelClientRequestPipeline
{
    /// <summary>
    /// 发送流式请求并校验状态。资源所有权归本管道：<paramref name="requestFactory"/>
    /// 每次尝试新建请求（内容随请求释放），成功响应交还调用方（后续释放归调用方），
    /// 失败路径（含错误正文读取）自行释放。
    /// <para>
    /// 重试仅覆盖"拿到成功响应头"之前（MaxRetries，可重试状态码与瞬时网络异常）；
    /// 响应体流一旦开始下发不再重试。建连与错误正文读取均受
    /// <paramref name="connectTimeout"/> 时间上限约束——错误正文此前只有大小限制、
    /// 已离开模型超时包装，上游悬挂可长期占用请求。
    /// </para>
    /// </summary>
    public static async Task<HttpResponseMessage> SendStreamRequestAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan connectTimeout,
        int maxRetries,
        CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = requestFactory();
                var response = await ModelClientRetry.WithTotalTimeout(
                    connectTimeout, ct,
                    token => httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;

                var statusCode = response.StatusCode;
                try
                {
                    string errorBody = await ModelClientRetry.WithTotalTimeout(
                        connectTimeout, ct,
                        token => BoundedResponseReader.ReadBodyAsync(response.Content, token)).ConfigureAwait(false);
                    if (ModelClientRetry.IsRetryable(statusCode) && attempt < maxRetries)
                    {
                        attempt++;
                        await ModelClientRetry.DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new ModelClientException(statusCode, errorBody);
                }
                finally
                {
                    // 错误正文读取自身超时/被取消的路径也必须释放，否则重试与异常传播泄漏已建连的
                    // response（与 OpenAICompatibleModelClient.StreamRawAsync 的失败建连收尾同语义）。
                    response.Dispose();
                }
            }
            catch (Exception ex) when (ModelClientRetry.IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                attempt++;
                await ModelClientRetry.DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
