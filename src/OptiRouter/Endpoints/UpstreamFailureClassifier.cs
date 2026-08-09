using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>Shared typed classification for upstream orchestration failures.</summary>
internal static class UpstreamFailureClassifier
{
    public static bool IsQuotaLimited(Exception? error)
        => error is ModelClientException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };

    public static string SafeMessage(Exception? error, bool quotaLimited)
        => quotaLimited ? "quota-exhausted" : error switch
        {
            ModelClientException mce => $"upstream-status-{(int)mce.StatusCode}",
            OperationCanceledException => "timeout",
            HttpRequestException => "network-error",
            _ => "upstream-error"
        };

    public static int GetStatus(Exception error) => error switch
    {
        ModelClientException mce => (int)mce.StatusCode,
        OperationCanceledException => 408,
        HttpRequestException => 503,
        _ => 502
    };
}
