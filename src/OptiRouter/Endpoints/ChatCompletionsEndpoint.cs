using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容 Chat Completions HTTP 端点。
/// 暴露 POST /v1/chat/completions，支持非流式与 SSE 流式两种模式。
/// </summary>
public static class ChatCompletionsEndpoint
{
    private static readonly JsonSerializerOptions SseOptions = new()
    {
        PropertyNamingPolicy = new JsonSnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 将 /v1/chat/completions 端点映射到路由图。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", async (ChatRequest request, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (request.Stream)
            {
                return Results.Stream(async stream =>
                {
                    try
                    {
                        await foreach (var chunk in orchestrator.StreamAsync(request, ct).ConfigureAwait(false))
                        {
                            var json = JsonSerializer.Serialize(chunk, SseOptions);
                            var line = $"data: {json}\n\n";
                            var bytes = Encoding.UTF8.GetBytes(line);
                            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        }
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct).ConfigureAwait(false);
                    }
                    catch (BudgetExhaustedException)
                    {
                        // intentional-simple：流式预算耗尽无法改变 HTTP 状态码，
                        // 发送一条 error event 后正常结束流。
                        var errorJson = JsonSerializer.Serialize(new { error = "budget exhausted" });
                        var line = $"data: {errorJson}\n\n";
                        var bytes = Encoding.UTF8.GetBytes(line);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct).ConfigureAwait(false);
                    }
                    catch (AllCandidatesFailedException)
                    {
                        // intentional-simple：流式全失败同理，发 error event 后结束。
                        var errorJson = JsonSerializer.Serialize(new { error = "all model candidates failed" });
                        var line = $"data: {errorJson}\n\n";
                        var bytes = Encoding.UTF8.GetBytes(line);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct).ConfigureAwait(false);
                    }
                }, "text/event-stream");
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct).ConfigureAwait(false);
                return Results.Ok(response);
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
        });

        return app;
    }
}
