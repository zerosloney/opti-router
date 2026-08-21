namespace OptiRouter.Clients;

/// <summary>
/// 模型客户端共享重试策略（OpenAI/Anthropic/Gemini 三协议一致）：
/// 可重试状态码、可重试异常判定与指数退避 + 抖动，保证 MaxRetries 端点配置语义跨协议一致。
/// </summary>
internal static class ModelClientRetry
{
    public static bool IsRetryable(System.Net.HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        // 429 is intentionally surfaced to request-level orchestration so quota
        // state is updated and another candidate can be selected immediately.
        return code is 408 or >= 500 and <= 599;
    }

    public static bool IsExceptionRetryable(Exception ex)
    {
        // HttpRequestException（DNS/连接/RST 等网络错）→ 重试。
        if (ex is HttpRequestException)
            return true;

        // HttpClient 超时抛 TaskCanceledException/OperationCanceledException，InnerException 为 TimeoutException。
        // 此为客户端内部超时（瞬时），应重试；外部 cancellationToken 主动取消（无 TimeoutException inner）不重试。
        if (ex is OperationCanceledException)
            return ex.InnerException is TimeoutException;

        return false;
    }

    /// <summary>指数退避 + 抖动：base = 2^attempt * 100 ms，附加 0~100 ms 抖动。</summary>
    public static async Task DelayWithJitterAsync(int attempt, CancellationToken cancellationToken)
    {
        int baseDelayMs = (int)Math.Pow(2, attempt) * 100;
        int jitterMs = Random.Shared.Next(0, 100);
        await Task.Delay(baseDelayMs + jitterMs, cancellationToken).ConfigureAwait(false);
    }
}
