using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容的 <c>GET /v1/models</c> 端点，列出当前启用的模型，供客户端发现可用模型。
/// </summary>
/// <remarks>
/// 受 /v1/* 鉴权与限流中间件保护。仅返回 <c>Enabled=true</c> 的模型，脱敏（不含 ApiKey/BaseUrl）。
/// </remarks>
public static class ModelsEndpoint
{
    /// <summary>
    /// 将 /v1/models 端点映射到路由图。
    /// </summary>
    public static IEndpointRouteBuilder MapModelsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (IOptions<OptiRouter.Configuration.RouterOptions> options) =>
        {
            // created 用固定纪元 0（无持久化创建时间）；owned_by 标识由本路由代理。
            var data = options.Value.Models
                .Where(m => m.Enabled)
                .Select(m => new
                {
                    id = m.Name,
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    // 扩展字段：帮助客户端按能力/上下文选择。OpenAI 客户端会忽略未知字段。
                    tier = m.Tier.ToString().ToLowerInvariant(),
                    max_context_tokens = m.MaxContextTokens,
                    tags = m.Tags
                })
                .ToList();

            return Results.Json(new { @object = "list", data });
        });

        return app;
    }
}
