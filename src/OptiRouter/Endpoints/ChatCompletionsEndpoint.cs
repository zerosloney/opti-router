using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容 Chat Completions HTTP 端点。
/// 暴露 POST /v1/chat/completions，支持非流式与 SSE 流式两种模式，透明透传上游原始响应。
/// </summary>
public static class ChatCompletionsEndpoint
{
    /// <summary>
    /// 将 /v1/chat/completions 端点映射到路由图。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", async (ChatRequest request, HttpContext httpContext, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (TryGetValidationError(request, out var validationError))
            {
                return Results.Problem(
                    title: "Invalid request",
                    detail: validationError,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string? sessionId = httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
                ? sid.ToString()
                : null;

            if (request.Stream)
            {
                IAsyncEnumerator<RawStreamLine>? enumerator = null;
                try
                {
                    enumerator = orchestrator.StreamAsync(request, ct, sessionId).GetAsyncEnumerator(ct);
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                        return Results.Stream(
                            stream => WriteDoneAsync(stream, ct),
                            "text/event-stream");
                    }

                    var firstLine = enumerator.Current;
                    var streamEnumerator = enumerator;
                    return Results.Stream(async stream =>
                    {
                        try
                        {
                            await WriteLineAsync(stream, firstLine, ct).ConfigureAwait(false);
                            while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                await WriteLineAsync(stream, streamEnumerator.Current, ct).ConfigureAwait(false);
                            }
                        }
                        catch (BudgetExhaustedException)
                        {
                            await WriteErrorAsync(stream, "budget exhausted", "BUDGET_EXHAUSTED", ct).ConfigureAwait(false);
                        }
                        catch (AllCandidatesFailedException)
                        {
                            await WriteErrorAsync(stream, "all model candidates failed", "ALL_CANDIDATES_FAILED", ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await WriteErrorAsync(stream, ex.Message, "INTERNAL_ERROR", ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                        }
                    }, "text/event-stream");
                }
                catch (ModelClientException ex)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateUpstreamRejection(ex, httpContext);
                }
                catch (BudgetExhaustedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("budget exhausted", "BUDGET_EXHAUSTED", ct);
                }
                catch (AllCandidatesFailedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("all model candidates failed", "ALL_CANDIDATES_FAILED", ct);
                }
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
                // 透明透传：直接回传上游原始 JSON，不 re-serialize。
                return Results.Content(response.Body, "application/json", Encoding.UTF8);
            }
            catch (BudgetExhaustedException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                httpContext.Response.Headers["Retry-After"] = "3600";

                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Budget exhausted",
                    Detail = ex.Message,
                    Status = StatusCodes.Status429TooManyRequests
                };
                problem.Extensions["code"] = "BUDGET_EXHAUSTED";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["retryAfterSeconds"] = 3600;

                return Results.Json(problem, statusCode: StatusCodes.Status429TooManyRequests, contentType: "application/problem+json");
            }
            catch (AllCandidatesFailedException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "All model candidates failed",
                    Detail = $"Attempted: {string.Join(", ", ex.AttemptedModels)}. Last failure: Model '{ex.LastModelName}' returned status {ex.LastStatusCode}.",
                    Status = StatusCodes.Status503ServiceUnavailable
                };
                problem.Extensions["code"] = "ALL_CANDIDATES_FAILED";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["attemptedModels"] = ex.AttemptedModels;
                if (ex.LastModelName != null)
                {
                    problem.Extensions["lastError"] = new Dictionary<string, object?>
                    {
                        ["model"] = ex.LastModelName,
                        ["statusCode"] = ex.LastStatusCode,
                        ["message"] = ex.LastErrorMessage
                    };
                }

                return Results.Json(problem, statusCode: StatusCodes.Status503ServiceUnavailable, contentType: "application/problem+json");
            }
            catch (ModelClientException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Upstream request rejected",
                    Status = (int)ex.StatusCode
                };
                problem.Extensions["code"] = "UPSTREAM_REJECTION";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["statusCode"] = (int)ex.StatusCode;

                return Results.Json(problem, statusCode: (int)ex.StatusCode, contentType: "application/problem+json");
            }
        });

        return app;
    }

    private static bool TryGetValidationError(ChatRequest request, out string error)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            error = "Messages must contain at least one item.";
        else if (request.Messages.Any(message => message is null || string.IsNullOrWhiteSpace(message.Role)))
            error = "Each message must have a role.";
        else if (request.Messages.Any(message => message.Content is null || message.Content.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            error = "Message content must not be null.";
        else if (request.Temperature is < 0 or > 2)
            error = "Temperature must be between 0 and 2.";
        else if (request.MaxTokens is <= 0)
            error = "MaxTokens must be greater than zero.";
        else
            error = string.Empty;

        return error.Length > 0;
    }

    private static IResult CreateUpstreamRejection(ModelClientException exception, HttpContext httpContext)
    {
        string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Title = "Upstream request rejected",
            Status = (int)exception.StatusCode
        };
        problem.Extensions["code"] = "UPSTREAM_REJECTION";
        if (requestId != null) problem.Extensions["requestId"] = requestId;
        problem.Extensions["statusCode"] = (int)exception.StatusCode;

        return Results.Json(problem, statusCode: (int)exception.StatusCode, contentType: "application/problem+json");
    }

    private static IResult CreateErrorStream(string error, string code, CancellationToken ct)
    {
        return Results.Stream(
            stream => WriteErrorAsync(stream, error, code, ct),
            "text/event-stream");
    }

    /// <summary>
    /// 透传原始 SSE data 行。客户端自己负责按 OpenAI 格式发送 [DONE]。
    /// </summary>
    private static async Task WriteLineAsync(Stream stream, RawStreamLine line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {line.Data}\n\n"), ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(Stream stream, string error, string code, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { error });
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), ct).ConfigureAwait(false);
        await WriteDoneAsync(stream, ct).ConfigureAwait(false);
    }

    private static Task WriteDoneAsync(Stream stream, CancellationToken ct)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct).AsTask();
    }
}
