using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容的 <c>GET /v1/models</c> 端点，列出当前启用的模型，供客户端发现可用模型。
/// </summary>
/// <remarks>
/// 受 /v1/* 鉴权与限流中间件保护。仅返回 <c>Enabled=true</c> 的模型，脱敏（不含 ApiKey/BaseUrl）。
/// 首位固定暴露虚拟模型 <c>auto</c>（智能路由别名）；真实模型的 <c>id</c> 统一为
/// 「{供应商}/{上游真实模型 Id}」格式（如 "deepseek/deepseek-chat"，同供应商同模型多 Key 追加 #2），
/// 请求该 id 时自动解析并转换为上游内部模型 ID 发送。
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
            var enabled = options.Value.Models.Where(m => m.Enabled).ToList();

            // 显示 ID 统一为 "{供应商}/{真实模型 Id}"；同基础 ID 重复时追加 " #2"、" #3"。
            var displayIds = ModelDisplayIds.Compute(enabled);

            var data = new List<object>
            {
                // 虚拟智能路由模型：请求 model="auto" 或缺省 model 时由 RouterEngine 全链路选择。
                new
                {
                    id = ExplicitModelPolicy.AutoAlias,
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    routing = "auto",
                    description = "Smart routing: OptiRouter selects the best enabled model per request.",
                    candidates = enabled.Count
                }
            };

            for (int i = 0; i < enabled.Count; i++)
            {
                var m = enabled[i];
                // 供应商：显式 Provider 配置优先，缺省从 BaseUrl 推断；同供应商多端点加序号区分。
                string provider = ModelDisplayIds.EffectiveProvider(m);

                // created 用固定纪元 0（无持久化创建时间）；owned_by 标识由本路由代理。
                data.Add(new
                {
                    id = displayIds[i],
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    // 指定该 id（或裸 upstream_id / 路由名 name）时固定直连；指定 auto 才走智能路由。
                    routing = "direct",
                    // 配置里的路由名（Dashboard/审计使用的内部标识）。
                    name = m.Name,
                    // 上游真实模型 id（发往供应商 API 的 model 值）。
                    upstream_id = m.UpstreamModelId,
                    // 扩展字段：帮助客户端按能力/上下文选择。OpenAI 客户端会忽略未知字段。
                    tier = m.Tier.ToString().ToLowerInvariant(),
                    provider,
                    family = m.Family,
                    max_context_tokens = m.MaxContextTokens,
                    tags = m.Tags
                });
            }

            return Results.Json(new { @object = "list", data });
        });

        return app;
    }
}
