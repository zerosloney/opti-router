using System.Net;

namespace OptiRouter.Clients;

/// <summary>
/// 模型客户端异常，封装非 2xx HTTP 状态码与响应体。
/// </summary>
public sealed class ModelClientException : Exception
{
    /// <summary>
    /// HTTP 状态码。
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// 响应体原文（可能为空）。
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// 初始化模型客户端异常。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="responseBody">响应体原文。</param>
    /// <param name="message">错误描述。</param>
    public ModelClientException(HttpStatusCode statusCode, string? responseBody, string? message = null)
        : base(message ?? $"Model client request failed with status code {(int)statusCode} ({statusCode}). Response body: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
