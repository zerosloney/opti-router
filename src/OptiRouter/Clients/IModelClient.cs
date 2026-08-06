using System.Runtime.CompilerServices;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// 模型客户端抽象，封装对 OpenAI 兼容接口的调用。
/// </summary>
public interface IModelClient
{
    /// <summary>
    /// 关联的端点配置。
    /// </summary>
    ModelEndpointOptions Endpoint { get; }

    /// <summary>
    /// 非流式调用，返回完整响应。
    /// </summary>
    /// <param name="request">请求体，Model 会被强制覆盖为端点配置的模型名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整聊天响应。</returns>
    /// <exception cref="ModelClientException">非 2xx 响应时抛出。</exception>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式调用，逐块返回响应增量。
    /// </summary>
    /// <param name="request">请求体，Model 会被强制覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应块异步枚举。</returns>
    /// <exception cref="ModelClientException">非 2xx 响应时抛出。</exception>
    IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 健康探测：发送最小请求，返回可用性与延迟。
    /// 失败时返回 false 且不抛异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>健康结果。</returns>
    Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 非流式调用，返回原始响应字符串（透明透传，保留上游全部字段）。
    /// </summary>
    /// <param name="request">请求体，Model 会被强制覆盖为端点配置的模型名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始响应 + 从中提取的 token 用量。</returns>
    /// <exception cref="ModelClientException">非 2xx 响应时抛出。</exception>
    Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式调用，逐行返回原始 SSE data 内容（透明透传，保留上游全部字段）。
    /// </summary>
    /// <param name="request">请求体，Model 会被强制覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始 SSE 行异步枚举；最后一行为 <c>[DONE]</c>。</returns>
    /// <exception cref="ModelClientException">非 2xx 响应时抛出。</exception>
    IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
