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

    /// <summary>
    /// 端点调用超时解析：TimeoutSeconds &gt; 0 生效，否则回退 120s（与共享 HttpClient 设 Infinite 前的工厂默认一致）。
    /// 非流式 = 总时长上限；流式 = 相邻 chunk 空闲上限（流式无总时长上限，推进中的流可跑任意久）。
    /// </summary>
    public static TimeSpan ResolveCallTimeout(Configuration.ModelEndpointOptions endpoint)
        => TimeSpan.FromSeconds(endpoint.TimeoutSeconds > 0 ? endpoint.TimeoutSeconds : 120);

    /// <summary>
    /// 非流式调用/流式建连阶段的总时长超时（共享 HttpClient 的 Timeout 已设 Infinite，见工厂注释）。
    /// 内部超时转抛 <see cref="TaskCanceledException"/>(inner: <see cref="TimeoutException"/>)——
    /// 与 HttpClient.Timeout 的原生异常签名一致，重试判定/上游故障分类零改动。
    /// 外部取消原样传播（无 TimeoutException inner，不重试）。
    /// </summary>
    public static async Task<T> WithTotalTimeout<T>(
        TimeSpan timeout, CancellationToken externalToken, Func<CancellationToken, Task<T>> operation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        cts.CancelAfter(timeout);
        try
        {
            return await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !externalToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"Model call exceeded timeout ({timeout.TotalSeconds:F0}s).", new TimeoutException());
        }
    }

    /// <summary>
    /// 流式空闲超时判定：读取等待期间超过 idleTimeout 无任何数据 → 抛
    /// TaskCanceledException(inner: TimeoutException)（与 HttpClient.Timeout 签名一致）。
    /// 每次读到数据后调用方应重建/重置 idle CTS 再调用本方法取读取令牌。
    /// </summary>
    public static CancellationToken IdleReadToken(
        ref CancellationTokenSource? idleCts, TimeSpan idleTimeout, CancellationToken externalToken)
    {
        if (idleCts is null)
        {
            idleCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        }
        else if (!idleCts.TryReset())
        {
            // 已触发的 CTS 无法重置（罕见竞态）：重建。
            idleCts.Dispose();
            idleCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        }
        idleCts.CancelAfter(idleTimeout);
        return idleCts.Token;
    }

    /// <summary>读取抛出的取消是否为空闲超时（而非外部取消）；配合 <see cref="IdleReadToken"/> 使用。</summary>
    public static bool IsIdleTimeout(CancellationTokenSource idleCts, CancellationToken externalToken)
        => idleCts.IsCancellationRequested && !externalToken.IsCancellationRequested;

    /// <summary>空闲超时的标准异常形态（与 HttpClient.Timeout 签名一致，分类零改动）。</summary>
    public static Exception IdleTimeoutException(TimeSpan idleTimeout) => new TaskCanceledException(
        $"Upstream stream idle for over {idleTimeout.TotalSeconds:F0}s (no data received).", new TimeoutException());
}
