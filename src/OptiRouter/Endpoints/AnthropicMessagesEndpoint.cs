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

            string? sessionId = ChatCompletionsEndpoint.ResolveSessionId(httpContext);

            if (request.Stream)
            {
                return await StreamAsync(orchestrator, request, sessionId, httpContext, ct).ConfigureAwait(false);
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
                string anthropicJson = AnthropicTranslators.ToAnthropicJson(response.Body);
                return Results.Content(anthropicJson, "application/json", Encoding.UTF8);
            }
            catch (BudgetExhaustedException)
            {
                // 日预算在 UTC 午夜重置，Retry-After 设为到午夜的剩余秒数。
                httpContext.Response.Headers["Retry-After"] = ProtocolErrorHelper.SecondsUntilUtcMidnight().ToString();
                return AnthropicError(StatusCodes.Status429TooManyRequests, "rate_limit_error", "budget exhausted");
            }
            catch (OptiRouter.Compliance.ComplianceViolationException ex)
            {
                return AnthropicError(StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
            }
            catch (AllCandidatesFailedException ex)
            {
                return AnthropicError(StatusCodes.Status503ServiceUnavailable, "api_error",
                    ProtocolErrorHelper.AllCandidatesFailedSummary(ex));
            }
            catch (ModelClientException ex)
            {
                return AnthropicError((int)ex.StatusCode, "api_error", $"Upstream request rejected (status {(int)ex.StatusCode}).");
            }
            catch (Exception ex)
            {
                // 非流式路径 catch-all：未预见异常兜底，返回 Anthropic 兼容 500 错误信封。
                // 未预见异常不外发 ex.Message（可能含内部细节），细节进服务端日志。
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "anthropic.messages");
                return AnthropicError(StatusCodes.Status500InternalServerError, "api_error", ProtocolErrorHelper.InternalErrorMessage);
            }
        });

        return app;
    }

    /// <summary>
    /// 流式路径外壳在 <see cref="ProtocolStreamPipeline"/>，这里只留 Anthropic 词汇：
    /// data 行 → 事件帧，异常 → <c>event: error</c> 帧。
    /// </summary>
    private static Task<IResult> StreamAsync(ProxyOrchestrator orchestrator, ChatRequest request, string? sessionId,
        HttpContext httpContext, CancellationToken ct)
    {
        var translator = new AnthropicTranslators.AnthropicStreamTranslator(request.Model);
        return ProtocolStreamPipeline.RunAsync(
            orchestrator,
            request,
            sessionId,
            () => string.Concat(new AnthropicTranslators.AnthropicStreamTranslator(request.Model).OnData("[DONE]")),
            data => translator.OnData(data),
            (ex, beforeFirstEvent) => FailureFrame(ex, httpContext, beforeFirstEvent),
            ct);
    }

    private static string FailureFrame(Exception ex, HttpContext httpContext, bool beforeFirstEvent)
    {
        switch (ex)
        {
            case BudgetExhaustedException:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("rate_limit_error", "budget exhausted");
            case AllCandidatesFailedException:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("api_error", "all model candidates failed");
            case OptiRouter.Compliance.ComplianceViolationException compliance:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("invalid_request_error", compliance.Message);
            // 首帧前被上游拒绝：客户端还没收到任何内容，给固定可重试文案
            case ModelClientException when beforeFirstEvent:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("api_error", "upstream request rejected before first event");
            case OperationCanceledException canceled:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("timeout_error", canceled.Message);
            // 流中上游故障（断流 502 检测/流内 error/连接失败）外发真实原因；
            // 首帧前的 HttpRequestException/IOException 落 default：不外发 ex.Message，细节进服务端日志。
            case var e when ProtocolStreamPipeline.IsUpstreamStreamFailure(e) && !beforeFirstEvent:
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("api_error", e.Message);
            default:
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "anthropic.messages");
                return AnthropicTranslators.AnthropicStreamTranslator.OnError("api_error", ProtocolErrorHelper.InternalErrorMessage);
        }
    }

    private static IResult AnthropicError(int status, string type, string message)
        => Results.Json(
            new { type = "error", error = new { type, message } },
            statusCode: status);
}
