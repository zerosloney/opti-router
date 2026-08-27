using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>Shared typed classification for upstream orchestration failures.</summary>
internal static class UpstreamFailureClassifier
{
    public static bool IsQuotaLimited(Exception? error)
        => error is ModelClientException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };

    /// <summary>
    /// 审计用错误摘要。ModelClientException 附带上游响应体首行（单行化 + 300 字符截断）：
    /// 4xx 的拒绝原因（上下文超限/参数不识别）与 429 的 retry-after 提示都只在 body 里，
    /// 丢掉它审计就只剩裸状态码（2026-08 排查 Kimi 400 时无现场可看，只能靠猜）。
    /// body 可能是整页 HTML，取首行防止撑爆 audit 列与请求页 UI。
    /// </summary>
    public static string SafeMessage(Exception? error, bool quotaLimited)
        => error switch
        {
            ModelClientException mce => Describe(mce, quotaLimited),
            OperationCanceledException => "timeout",
            HttpRequestException => "network-error",
            _ => quotaLimited ? "quota-exhausted" : "upstream-error"
        };

    private static string Describe(ModelClientException mce, bool quotaLimited)
    {
        string prefix = quotaLimited
            ? "quota-exhausted"
            : $"upstream-status-{(int)mce.StatusCode}";
        if (string.IsNullOrWhiteSpace(mce.ResponseBody))
            return prefix;
        string body = mce.ResponseBody;
        int newline = body.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
            body = body[..newline];
        if (body.Length > 300)
            body = body[..300] + "…";
        return $"{prefix}: {body}";
    }

    public static int GetStatus(Exception error) => error switch
    {
        ModelClientException mce => (int)mce.StatusCode,
        OperationCanceledException => 408,
        HttpRequestException => 503,
        _ => 502
    };
}
