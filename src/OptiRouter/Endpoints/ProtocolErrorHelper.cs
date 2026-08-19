using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Endpoints;

/// <summary>
/// 协议入口错误响应的最小共享实现。
/// </summary>
internal static class ProtocolErrorHelper
{
    /// <summary>
    /// 未预见异常（INTERNAL_ERROR/api_error/INTERNAL 兜底桶）对客户端的统一文案。
    /// ex.Message 可能携带内部细节（路径/连接信息），只进服务端日志，不外发。
    /// 分类桶（UPSTREAM_ERROR/TIMEOUT/RESPONSE_TOO_LARGE 等）的消息是运营性信息，不受此约束。
    /// </summary>
    internal const string InternalErrorMessage =
        "An unexpected internal error occurred. Include the request id when reporting this issue.";

    /// <summary>记录协议入口兜底捕获的未预见异常（客户端只收到通用文案，细节进服务端日志）。</summary>
    internal static void LogUnhandledProtocolError(HttpContext context, Exception ex, string protocol)
    {
        context.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("OptiRouter.Protocol")
            .LogError(ex, "Unhandled exception in {Protocol} request {Path}", protocol, context.Request.Path);
    }

    internal static object CreateOpenAiErrorPayload(
        string message,
        string code,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var error = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = message,
            ["type"] = OpenAiErrorType(code),
            ["code"] = code
        };

        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                if (!error.ContainsKey(key))
                    error[key] = value;
            }
        }

        return new Dictionary<string, object?>
        {
            ["error"] = error
        };
    }

    internal static IResult CreateOpenAiResult(
        int statusCode,
        string message,
        string code,
        IReadOnlyDictionary<string, object?>? details = null)
        => Results.Json(
            CreateOpenAiErrorPayload(message, code, details),
            statusCode: statusCode,
            contentType: "application/json");

    internal static async Task WriteProxyErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        string code,
        int retryAfterSeconds = 0)
    {
        context.Response.StatusCode = statusCode;
        if (retryAfterSeconds > 0)
        {
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (IsOpenAiPath(context.Request.Path))
        {
            await context.Response.WriteAsJsonAsync(
                CreateOpenAiErrorPayload(message, code),
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/v1/messages"))
        {
            await context.Response.WriteAsJsonAsync(
                new
                {
                    type = "error",
                    error = new
                    {
                        type = statusCode == StatusCodes.Status429TooManyRequests
                            ? "rate_limit_error"
                            : "authentication_error",
                        message
                    }
                },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/v1beta"))
        {
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = new
                    {
                        code = statusCode,
                        message,
                        status = statusCode == StatusCodes.Status429TooManyRequests
                            ? "RESOURCE_EXHAUSTED"
                            : "UNAUTHENTICATED"
                    }
                },
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static bool IsOpenAiPath(PathString path) =>
        path.StartsWithSegments("/v1") && !path.StartsWithSegments("/v1/messages");

    internal static string OpenAiErrorType(string code) => code switch
    {
        "BUDGET_EXHAUSTED" or "budget_exhausted" => "budget_exceeded",
        "ALL_CANDIDATES_FAILED" => "all_candidates_failed",
        "CONTENT_MODERATED" or "MODEL_NOT_FOUND" or "model_not_found" or "invalid_request_error"
            => "invalid_request_error",
        "UPSTREAM_REJECTION" => "upstream_error",
        "INVALID_API_KEY" => "authentication_error",
        "RATE_LIMIT_EXCEEDED" or "CONCURRENCY_LIMIT_EXCEEDED" => "rate_limit_error",
        "INTERNAL_ERROR" => "server_error",
        "UPSTREAM_ERROR" => "upstream_error",
        "TIMEOUT" => "timeout",
        "RESPONSE_TOO_LARGE" => "response_too_large",
        _ => "server_error"
    };
}
