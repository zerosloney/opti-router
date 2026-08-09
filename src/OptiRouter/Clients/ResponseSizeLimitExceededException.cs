namespace OptiRouter.Clients;

/// <summary>
/// 流式响应超出字节上限（<c>MaxResponseStreamBytes</c> 累计上限或 <c>MaxStreamLineBytes</c> 单行上限）时抛出。
/// </summary>
/// <remarks>
/// 与通用 <see cref="InvalidOperationException"/> 区分，使 endpoint 能精确分类为
/// <c>RESPONSE_TOO_LARGE</c> code（不可重试），而非笼统的 <c>INTERNAL_ERROR</c>。
/// 客户端据此判定：调高上限或排查上游输出，而非重试。
/// </remarks>
public sealed class ResponseSizeLimitExceededException : Exception
{
    /// <summary>
    /// 触发上限的字节阈值。
    /// </summary>
    public long LimitBytes { get; }

    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="limitBytes">触发上限的字节阈值。</param>
    /// <param name="message">错误消息。</param>
    public ResponseSizeLimitExceededException(long limitBytes, string message)
        : base(message)
    {
        LimitBytes = limitBytes;
    }
}
