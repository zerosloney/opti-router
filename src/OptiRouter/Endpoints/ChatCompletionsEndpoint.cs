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
                            await WriteErrorAsync(stream, "budget exhausted", ct).ConfigureAwait(false);
                        }
                        catch (AllCandidatesFailedException)
                        {
                            await WriteErrorAsync(stream, "all model candidates failed", ct).ConfigureAwait(false);
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
                    return CreateUpstreamRejection(ex);
                }
                catch (BudgetExhaustedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("budget exhausted", ct);
                }
                catch (AllCandidatesFailedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("all model candidates failed", ct);
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
                return Results.Problem(
                    title: "Budget exhausted",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (AllCandidatesFailedException ex)
            {
                return Results.Problem(
                    title: "All model candidates failed",
                    detail: $"Attempted: {string.Join(", ", ex.AttemptedModels)}",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ModelClientException ex)
            {
                return CreateUpstreamRejection(ex);
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
        else if (request.Messages.Any(message => message.Content is null))
            error = "Message content must not be null.";
        else if (request.Temperature is < 0 or > 2)
            error = "Temperature must be between 0 and 2.";
        else if (request.MaxTokens is <= 0)
            error = "MaxTokens must be greater than zero.";
        else
            error = string.Empty;

        return error.Length > 0;
    }

    private static IResult CreateUpstreamRejection(ModelClientException exception)
    {
        return Results.Problem(
            title: "Upstream request rejected",
            statusCode: (int)exception.StatusCode);
    }

    private static IResult CreateErrorStream(string error, CancellationToken ct)
    {
        return Results.Stream(
            stream => WriteErrorAsync(stream, error, ct),
            "text/event-stream");
    }

    /// <summary>
    /// 透传原始 SSE data 行。客户端自己负责按 OpenAI 格式发送 [DONE]。
    /// </summary>
    private static async Task WriteLineAsync(Stream stream, RawStreamLine line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {line.Data}\n\n"), ct).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(Stream stream, string error, CancellationToken ct)
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
