using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 模型配置 CRUD Dashboard 页面及 API。与监控 Dashboard (/dashboard) 职责分离。
/// </summary>
public static class ModelsConfigHandler
{
    /// <summary>
    /// 注册模型配置页 HTML 路由及 /api/models/* CRUD API。
    /// </summary>
    public static void MapModelsConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 1. Models Config UI is now served by Blazor Server via Pages/Models/_Host.cshtml (Razor Pages routing).
        //    Old MapGet removed - was: static models.html served here.

        // 2. GET all (不暴露完整 ApiKey，只返回是否已配置)
        endpoints.MapGet("/api/models", (ModelsConfigService cfg) =>
        {
            var models = cfg.LoadModels().Select(m => new
            {
                m.Name,
                m.Id,
                m.BaseUrl,
                m.Provider,
                m.Family,
                Tier = m.Tier.ToString(),
                m.MaxContextTokens,
                m.TimeoutSeconds,
                m.MaxRetries,
                m.Enabled,
                m.IsLocalOrPrivate,
                m.InputPricePerMillion,
                m.CachedInputPricePerMillion,
                m.CacheWriteInputPricePerMillion,
                m.OutputPricePerMillion,
                m.Tags,
                HasApiKey = !string.IsNullOrEmpty(m.ApiKey)
            }).ToList();
            return Results.Json(models);
        });

        // 3. GET full JSON (用于编辑表单；剥除 ApiKey 明文，仅保留是否已配置)
        endpoints.MapGet("/api/models/raw", (ModelsConfigService cfg) =>
        {
            var models = cfg.LoadModels().Select(m => new
            {
                m.Name,
                m.Id,
                m.BaseUrl,
                Tier = m.Tier.ToString(),
                m.MaxContextTokens,
                m.TimeoutSeconds,
                m.MaxRetries,
                m.Enabled,
                m.IsLocalOrPrivate,
                m.Provider,
                m.Family,
                m.InputPricePerMillion,
                m.CachedInputPricePerMillion,
                m.CacheWriteInputPricePerMillion,
                m.OutputPricePerMillion,
                m.Tags,
                HasApiKey = !string.IsNullOrEmpty(m.ApiKey)
            });
            return Results.Json(new { models, configFile = cfg.ConfigFilePath });
        });

        // 4. POST create new model
        endpoints.MapPost("/api/models", (ModelsConfigService cfg, CreateModelRequest req) =>
        {
            // Name 与 Id 至少提供一个；只提供 Id 时自动生成「供应商/模型」路由名（冲突追加序号）。
            if (string.IsNullOrWhiteSpace(req.Name) && string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest(new { error = "Model name or id is required" });
            if (string.IsNullOrWhiteSpace(req.BaseUrl))
                return Results.BadRequest(new { error = "BaseUrl is required" });

            var model = new ModelEndpointOptions
            {
                Name = req.Name?.Trim() ?? string.Empty,
                Id = req.Id?.Trim() ?? string.Empty,
                BaseUrl = req.BaseUrl.Trim().TrimEnd('/'),
                ApiKey = req.ApiKey,
                Provider = req.Provider?.Trim() ?? string.Empty,
                Family = req.Family?.Trim() ?? string.Empty,
                Tier = req.Tier ?? ModelTier.Medium,
                MaxContextTokens = (req.MaxContextTokens is > 0) ? req.MaxContextTokens.Value : 8192,
                InputPricePerMillion = (req.InputPricePerMillion ?? 0) < 0 ? 0 : req.InputPricePerMillion!.Value,
                OutputPricePerMillion = (req.OutputPricePerMillion ?? 0) < 0 ? 0 : req.OutputPricePerMillion!.Value,
                CachedInputPricePerMillion = req.CachedInputPricePerMillion is >= 0 ? req.CachedInputPricePerMillion : null,
                CacheWriteInputPricePerMillion = req.CacheWriteInputPricePerMillion is >= 0 ? req.CacheWriteInputPricePerMillion : null,
                TimeoutSeconds = (req.TimeoutSeconds is > 0) ? req.TimeoutSeconds.Value : 120,
                MaxRetries = (req.MaxRetries is >= 0) ? req.MaxRetries.Value : 0,
                Enabled = req.Enabled ?? true,
                IsLocalOrPrivate = req.IsLocalOrPrivate ?? false
            };
            if (req.Tags is not null)
                foreach (var tag in req.Tags)
                    model.Tags.Add(tag);

            // 与既有模型一起归一化：生成名 + 冲突去重（如 deepseek/deepseek-chat #2）。
            var existing = cfg.LoadModels();
            var pending = new List<ModelEndpointOptions>(existing) { model };
            ModelNameNormalizer.Normalize(pending);

            if (string.IsNullOrWhiteSpace(model.Name))
                return Results.BadRequest(new { error = "Model name is required when id is absent" });
            if (existing.Any(m => string.Equals(m.Name, model.Name, StringComparison.Ordinal)))
                return Results.Conflict(new { error = $"Model '{model.Name}' already exists" });

            try
            {
                cfg.UpsertModel(model);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Created($"/api/models/{model.Name}", new { message = $"Model '{model.Name}' created", model = new { model.Name, model.Id, model.BaseUrl, model.Tier, model.Enabled } });
        });

        // 5. PUT update single model (持久化到文件 + 热重载)
        endpoints.MapPut("/api/models/{name}", (string name, ModelsConfigService cfg, UpdateModelRequest req) =>
        {
            var models = cfg.LoadModels();
            var model = models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (model is null)
                return Results.NotFound(new { error = $"Model '{name}' not found" });

            if (req.BaseUrl is not null && !string.IsNullOrWhiteSpace(req.BaseUrl)) model.BaseUrl = req.BaseUrl.TrimEnd('/');
            if (req.Id is not null) model.Id = req.Id.Trim(); // 空字符串表示清除（回退 Name 作上游 id）
            if (req.ApiKey is not null) model.ApiKey = req.ApiKey; // 空字符串表示清除
            if (req.Tier is not null) model.Tier = req.Tier.Value;
            if (req.MaxContextTokens is > 0) model.MaxContextTokens = req.MaxContextTokens.Value;
            if (req.TimeoutSeconds is > 0) model.TimeoutSeconds = req.TimeoutSeconds.Value;
            if (req.MaxRetries is >= 0) model.MaxRetries = req.MaxRetries.Value;
            if (req.Enabled is not null) model.Enabled = req.Enabled.Value;
            if (req.IsLocalOrPrivate is not null) model.IsLocalOrPrivate = req.IsLocalOrPrivate.Value;
            if (req.Provider is not null) model.Provider = req.Provider.Trim();
            if (req.Family is not null) model.Family = req.Family.Trim();
            if (req.InputPricePerMillion >= 0) model.InputPricePerMillion = req.InputPricePerMillion.Value;
            if (req.OutputPricePerMillion >= 0) model.OutputPricePerMillion = req.OutputPricePerMillion.Value;
            if (req.CachedInputPricePerMillion >= 0) model.CachedInputPricePerMillion = req.CachedInputPricePerMillion.Value;
            if (req.CacheWriteInputPricePerMillion >= 0) model.CacheWriteInputPricePerMillion = req.CacheWriteInputPricePerMillion.Value;

            try
            {
                cfg.UpsertModel(model);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Ok(new { message = $"Model '{name}' updated", model = new { model.Name, model.Id, model.BaseUrl, model.Tier, model.MaxContextTokens, model.TimeoutSeconds, model.MaxRetries, model.Enabled, model.InputPricePerMillion, model.OutputPricePerMillion, HasApiKey = !string.IsNullOrEmpty(model.ApiKey) } });
        });

        // 6. DELETE remove model
        endpoints.MapDelete("/api/models/{name}", (string name, ModelsConfigService cfg) =>
        {
            bool deleted = cfg.DeleteModel(name);
            if (!deleted)
                return Results.NotFound(new { error = $"Model '{name}' not found" });
            return Results.Ok(new { message = $"Model '{name}' deleted" });
        });

        // 7. POST Test Endpoint Connectivity
        endpoints.MapPost("/api/models/{name}/test", async (string name, ModelsConfigService cfg, IModelClientProvider clientProvider) =>
        {
            var models = cfg.LoadModels();
            var model = models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (model is null)
                return Results.NotFound(new { success = false, error = $"Model '{name}' not found" });

            var client = clientProvider.GetClient(model);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, model.TimeoutSeconds)));
            var result = await client.ProbeAsync(cts.Token);
            return Results.Ok(new
            {
                success = result.Healthy,
                latencyMs = (long)result.LatencyMs,
                message = result.Healthy ? "连接正常 (OK)" : "连接异常",
                error = result.Error
            });
        });
    }

    private record UpdateModelRequest(
        string? BaseUrl,
        string? ApiKey,
        ModelTier? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion,
        string? Provider = null,
        string? Family = null,
        string? Id = null,
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool? IsLocalOrPrivate = null);

    private record CreateModelRequest(
        string? Name,
        string BaseUrl,
        string? ApiKey,
        ModelTier? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion,
        List<string>? Tags,
        string? Provider = null,
        string? Family = null,
        string? Id = null,
        decimal? CachedInputPricePerMillion = null,
        decimal? CacheWriteInputPricePerMillion = null,
        bool? IsLocalOrPrivate = null);
}
