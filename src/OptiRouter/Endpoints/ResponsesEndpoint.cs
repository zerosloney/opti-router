using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI Responses API（POST /v1/responses）兼容 HTTP 端点。
/// <para>
/// 接收 OpenAI Responses 原生请求（input/instructions/max_output_tokens 等字段），
/// 翻译为内部 OpenAI 契约 ChatRequest 进路由管线，响应（非流式 JSON / 流式 SSE 事件序列）
/// 再翻译回 Responses 格式。鉴权复用 /v1/* 代理鉴权（Authorization: Bearer 或 x-api-key）。
/// </para>
/// </summary>
public static class ResponsesEndpoint
{
    /// <summary>
    /// 将 /v1/responses 端点映射到路由图。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapResponses(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/responses", async (HttpContext httpContext,
            IOptionsMonitor<RouterOptions> optionsMonitor,
            ProxyOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            string body;
            using (var reader = new StreamReader(httpContext.Request.Body))
            {
                body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            ChatRequest request;
            try
            {
                request = OpenAiResponsesTranslators.FromResponsesJson(body);
            }
            catch (JsonException ex)
            {
                return ResponsesError(StatusCodes.Status400BadRequest, "invalid_request_error",
                    $"Invalid request body: {ex.Message}");
            }

            if (!ChatCompletionsEndpoint.IsKnownModel(request.Model, optionsMonitor.CurrentValue))
            {
                return ResponsesError(StatusCodes.Status404NotFound, "model_not_found",
                    $"model: The model '{request.Model}' does not exist or is not enabled. " +
                    "Use 'auto' for smart routing, or GET /v1/models for available model ids.");
            }

            string? sessionId = ChatCompletionsEndpoint.ResolveSessionId(httpContext);

            bool stream = request.Stream;
            if (stream)
            {
                return await StreamAsync(orchestrator, request, sessionId, httpContext, ct).ConfigureAwait(false);
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
                string responsesJson = OpenAiResponsesTranslators.ToResponsesJson(response.Body);
                return Results.Content(responsesJson, "application/json", Encoding.UTF8);
            }
            catch (BudgetExhaustedException)
            {
                // 日预算在 UTC 午夜重置，Retry-After 设为到午夜的剩余秒数。
                httpContext.Response.Headers["Retry-After"] = ProtocolErrorHelper.SecondsUntilUtcMidnight().ToString();
                return ResponsesError(StatusCodes.Status429TooManyRequests, "BUDGET_EXHAUSTED", "budget exhausted");
            }
            catch (OptiRouter.Compliance.ComplianceViolationException ex)
            {
                return ResponsesError(StatusCodes.Status400BadRequest, "invalid_request_error", ex.Message);
            }
            catch (AllCandidatesFailedException ex)
            {
                return ResponsesError(StatusCodes.Status503ServiceUnavailable, "ALL_CANDIDATES_FAILED",
                    ProtocolErrorHelper.AllCandidatesFailedSummary(ex));
            }
            catch (ModelClientException ex)
            {
                return ResponsesError((int)ex.StatusCode, "UPSTREAM_REJECTION",
                    $"Upstream request rejected (status {(int)ex.StatusCode}).");
            }
            catch (Exception ex)
            {
                // 非流式路径 catch-all：未预见异常兜底，返回 OpenAI 兼容 500 错误信封。
                // 未预见异常不外发 ex.Message（可能含内部细节），细节进服务端日志。
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "openai.responses");
                return ResponsesError(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR",
                    ProtocolErrorHelper.InternalErrorMessage);
            }
        });

        return app;
    }

    /// <summary>
    /// 流式路径外壳在 <see cref="ProtocolStreamPipeline"/>，这里只留 Responses 词汇：
    /// data 行 → <c>event:/data:</c> 帧序列，异常 → <c>event: error</c> 帧。
    /// </summary>
    private static Task<IResult> StreamAsync(ProxyOrchestrator orchestrator, ChatRequest request, string? sessionId,
        HttpContext httpContext, CancellationToken ct)
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator();
        return ProtocolStreamPipeline.RunAsync(
            orchestrator,
            request,
            sessionId,
            () => string.Concat(new OpenAiResponsesTranslators.ResponsesStreamTranslator().OnData("[DONE]")),
            data => translator.OnData(data),
            (ex, beforeFirstEvent) => FailureFrame(ex, httpContext, beforeFirstEvent),
            ct);
    }

    private static string FailureFrame(Exception ex, HttpContext httpContext, bool beforeFirstEvent)
    {
        switch (ex)
        {
            case BudgetExhaustedException:
                return ErrorFrame("rate_limit_error", "budget exhausted");
            case AllCandidatesFailedException:
                return ErrorFrame("api_error", "all model candidates failed");
            case OptiRouter.Compliance.ComplianceViolationException compliance:
                return ErrorFrame("invalid_request_error", compliance.Message);
            // 首帧前被上游拒绝：客户端还没收到任何内容，给固定可重试文案
            case ModelClientException when beforeFirstEvent:
                return ErrorFrame("api_error", "upstream request rejected before first event");
            case OperationCanceledException canceled:
                return ErrorFrame("timeout_error", canceled.Message);
            // 流中上游故障（断流 502 检测/流内 error/连接失败）外发真实原因；
            // 首帧前的 HttpRequestException/IOException 落 default：不外发 ex.Message，细节进服务端日志。
            case var e when ProtocolStreamPipeline.IsUpstreamStreamFailure(e) && !beforeFirstEvent:
                return ErrorFrame("api_error", e.Message);
            default:
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "openai.responses");
                return ErrorFrame("api_error", ProtocolErrorHelper.InternalErrorMessage);
        }
    }

    private static string ErrorFrame(string code, string message)
    {
        // Responses 流内错误是独立的 event: error 帧（chat 才用 data: {error} 兜底）。
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["code"] = code,
            ["message"] = message
        };
        return OpenAiResponsesTranslators.ResponsesStreamTranslator.Frame("error", payload);
    }

    private static IResult ResponsesError(int status, string code, string message)
        => ProtocolErrorHelper.CreateOpenAiResult(status, message, code);
}