using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// Anthropic Messages API 兼容 HTTP 端点。
/// 暴露 POST /v1/messages，接收 Anthropic 原生请求，翻译为内部 OpenAI 契约进路由管线，
/// 响应（非流式 JSON / 流式 SSE 事件序列）再翻译回 Anthropic 格式。
/// 鉴权复用 /v1/* 代理鉴权：Authorization: Bearer 或 Anthropic 原生 x-api-key 头。
/// </summary>
public static class AnthropicMessagesEndpoint
{
    /// <summary>
    /// 将 /v1/messages 端点映射到路由图。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapAnthropicMessages(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", async (HttpContext httpContext, IOptionsMonitor<OptiRouter.Configuration.RouterOptions> optionsMonitor, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            string body;
            using (var reader = new StreamReader(httpContext.Request.Body))
            {
                body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            ChatRequest request;
            try
            {
                request = AnthropicTranslators.FromAnthropicJson(body);
            }
            catch (JsonException ex)
            {
                return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", $"Invalid JSON body: {ex.Message}");
            }

            if (request.Messages.Count == 0)
            {
                return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", "messages: at least one message is required.");
            }
            if (request.MaxTokens is not > 0)
            {
                return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", "max_tokens: field is required and must be greater than zero.");
            }
            if (!ChatCompletionsEndpoint.IsKnownModel(request.Model, optionsMonitor.CurrentValue))
            {
                return AnthropicError(StatusCodes.Status404NotFound, "invalid_request_error",
                    $"model: The model '{request.Model}' does not exist or is not enabled. Use 'auto' for smart routing, or GET /v1/models for available model ids.");
            }

            string? sessionId = httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
                ? sid.ToString()
                : null;

            if (request.Stream)
            {
                return await StreamAsync(orchestrator, request, sessionId, ct).ConfigureAwait(false);
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
                string anthropicJson = AnthropicTranslators.ToAnthropicJson(response.Body);
                return Results.Content(anthropicJson, "application/json", Encoding.UTF8);
            }
            catch (BudgetExhaustedException)
            {
                httpContext.Response.Headers["Retry-After"] = "3600";
                return AnthropicError(StatusCodes.Status429TooManyRequests, "rate_limit_error", "budget exhausted");
            }
            catch (OptiRouter.Compliance.ComplianceViolationException ex)
            {
                return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
            }
            catch (AllCandidatesFailedException ex)
            {
                return AnthropicError(StatusCodes.Status503ServiceUnavailable, "api_error",
                    $"Attempted: {string.Join(", ", ex.AttemptedModels)}. Last failure: Model '{ex.LastModelName}' returned status {ex.LastStatusCode}.");
            }
            catch (ModelClientException ex)
            {
                return AnthropicError((int)ex.StatusCode, "api_error", $"Upstream request rejected (status {(int)ex.StatusCode}).");
            }
        });

        return app;
    }

    private static async Task<IResult> StreamAsync(ProxyOrchestrator orchestrator, ChatRequest request, string? sessionId, CancellationToken ct)
    {
        IAsyncEnumerator<RawStreamLine>? enumerator = null;
        try
        {
            enumerator = orchestrator.StreamAsync(request, ct, sessionId).GetAsyncEnumerator(ct);
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                // 空流也要输出完整 Anthropic 事件序列，客户端才能正常收尾
                var emptyTranslator = new AnthropicTranslators.AnthropicStreamTranslator(request.Model);
                string payload = string.Join(string.Empty, emptyTranslator.OnData("[DONE]"));
                return Results.Stream(
                    stream => stream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct).AsTask(),
                    "text/event-stream");
            }

            var translator = new AnthropicTranslators.AnthropicStreamTranslator(request.Model);
            var firstLine = enumerator.Current;
            var streamEnumerator = enumerator;
            return Results.Stream(async stream =>
            {
                try
                {
                    foreach (string block in translator.OnData(firstLine.Data))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(block), ct).ConfigureAwait(false);
                    }
                    while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        foreach (string block in translator.OnData(streamEnumerator.Current.Data))
                        {
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(block), ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (BudgetExhaustedException)
                {
                    await WriteStreamErrorAsync(stream, "rate_limit_error", "budget exhausted", ct).ConfigureAwait(false);
                }
                catch (AllCandidatesFailedException)
                {
                    await WriteStreamErrorAsync(stream, "api_error", "all model candidates failed", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 与 OpenAI 端点同一分类思路：取消/超时 → timeout_error 可重试；其余 → api_error
                    string type = ex is OperationCanceledException ? "timeout_error" : "api_error";
                    await WriteStreamErrorAsync(stream, type, ex.Message, ct).ConfigureAwait(false);
                }
                finally
                {
                    await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                }
            }, "text/event-stream");
        }
        catch (ModelClientException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, "api_error", "upstream request rejected before first event", ct),
                "text/event-stream");
        }
        catch (BudgetExhaustedException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, "rate_limit_error", "budget exhausted", ct),
                "text/event-stream");
        }
        catch (AllCandidatesFailedException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, "api_error", "all model candidates failed", ct),
                "text/event-stream");
        }
    }

    private static Task WriteStreamErrorAsync(Stream stream, string type, string message, CancellationToken ct)
        => stream.WriteAsync(Encoding.UTF8.GetBytes(AnthropicTranslators.AnthropicStreamTranslator.OnError(type, message)), ct).AsTask();

    private static IResult AnthropicError(int status, string type, string message)
        => Results.Json(
            new { type = "error", error = new { type, message } },
            statusCode: status);
}
