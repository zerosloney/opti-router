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

        // 2. GET all (不暴露完整 ApiKey，只返回是否已配置 + 遮蔽预览)
        endpoints.MapGet("/api/models", (ModelsConfigService cfg) =>
        {
            var models = cfg.LoadModels();
            var names = EffectiveNames(models);
            var data = models.Select((m, i) => new
            {
                Name = names[i],
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
                HasApiKey = !string.IsNullOrEmpty(m.ApiKey),
                ApiKeyHint = BuildApiKeyHint(m.ApiKey)
            }).ToList();
            return Results.Json(data);
        });

        // 3. GET full JSON (用于编辑表单；剥除 ApiKey 明文，仅保留是否已配置)
        endpoints.MapGet("/api/models/raw", (ModelsConfigService cfg) =>
        {
            var models = cfg.LoadModels();
            var names = EffectiveNames(models);
            var data = models.Select((m, i) => new
            {
                Name = names[i],
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
                HasApiKey = !string.IsNullOrEmpty(m.ApiKey),
                ApiKeyHint = BuildApiKeyHint(m.ApiKey)
            });
            return Results.Json(new { models = data, configStore = "sqlite" });
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
                Tier = Enum.TryParse<ModelTier>(req.Tier, ignoreCase: true, out var tier) ? tier : ModelTier.Medium,
                MaxContextTokens = (req.MaxContextTokens is > 0) ? req.MaxContextTokens.Value : 200_000,
                InputPricePerMillion = Math.Max(0m, req.InputPricePerMillion ?? 0m),
                OutputPricePerMillion = Math.Max(0m, req.OutputPricePerMillion ?? 0m),
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
        // 显示名含 "/"（如 "sensenova/deepseek-chat"），无法作为路由段，另提供 ?name= 查询参数形式。
        endpoints.MapPut("/api/models/{name}", (string name, ModelsConfigService cfg, UpdateModelRequest req)
            => UpdateModel(name, cfg, req));
        endpoints.MapPut("/api/models", (string name, ModelsConfigService cfg, UpdateModelRequest req)
            => UpdateModel(name, cfg, req));

        // 6. DELETE remove model（?name= 形式同上，兼容含 "/" 的显示名）
        endpoints.MapDelete("/api/models/{name}", (string name, ModelsConfigService cfg)
            => DeleteModel(name, cfg));
        endpoints.MapDelete("/api/models", (string name, ModelsConfigService cfg)
            => DeleteModel(name, cfg));

        // 7. POST Test Endpoint Connectivity
        // 显示名含 "/"（如 "sensenova/deepseek-chat"），无法作为路由段，另提供 ?name= 查询参数形式。
        endpoints.MapPost("/api/models/{name}/test", (string name, ModelsConfigService cfg, IModelClientProvider clientProvider)
            => TestEndpointConnectivity(name, cfg, clientProvider));
        endpoints.MapPost("/api/models/test", (string name, ModelsConfigService cfg, IModelClientProvider clientProvider)
            => TestEndpointConnectivity(name, cfg, clientProvider));

        // 2b. GET single model ApiKey（管理员按需查看完整密钥；列表只下发遮蔽预览，避免批量泄露面）。
        endpoints.MapGet("/api/models/{name}/apikey", (string name, ModelsConfigService cfg)
            => RevealApiKey(name, cfg));
        endpoints.MapGet("/api/models/apikey", (string name, ModelsConfigService cfg)
            => RevealApiKey(name, cfg));
    }

    private static IResult RevealApiKey(string name, ModelsConfigService cfg)
    {
        var models = cfg.LoadModels();
        var names = EffectiveNames(models);
        int idx = names.FindIndex(n => string.Equals(n, name, StringComparison.Ordinal));
        if (idx < 0)
            return Results.NotFound(new { error = $"Model '{name}' not found" });
        return Results.Ok(new { apiKey = models[idx].ApiKey ?? "" });
    }

    /// <summary>
    /// 生成 ApiKey 遮蔽预览：前 3 + 后 4（长度 ≤ 8 只露前 2），供管理员在列表中核对配置。
    /// 完整密钥不回传前端；检测到首尾空白时附加警示——粘贴误差是上游 401 的常见根因。
    /// </summary>
    internal static string? BuildApiKeyHint(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;

        string hint = apiKey.Length <= 8
            ? apiKey[..2] + "••••"
            : apiKey[..3] + "••••" + apiKey[^4..];
        if (apiKey != apiKey.Trim())
            hint += " ⚠含首尾空白";
        return hint;
    }

    private static IResult UpdateModel(string name, ModelsConfigService cfg, UpdateModelRequest req)
    {
        var models = cfg.LoadModels();
        var names = EffectiveNames(models);
        int idx = names.FindIndex(n => string.Equals(n, name, StringComparison.Ordinal));
        if (idx < 0)
            return Results.NotFound(new { error = $"Model '{name}' not found" });
        var model = models[idx];

        if (req.BaseUrl is not null && !string.IsNullOrWhiteSpace(req.BaseUrl)) model.BaseUrl = req.BaseUrl.TrimEnd('/');
        if (req.Id is not null) model.Id = req.Id.Trim(); // 空字符串表示清除（回退 Name 作上游 id）
        if (req.ApiKey is not null) model.ApiKey = req.ApiKey; // 空字符串表示清除
        if (req.Tier is not null && Enum.TryParse<ModelTier>(req.Tier, ignoreCase: true, out var tier)) model.Tier = tier;
        if (req.MaxContextTokens is > 0) model.MaxContextTokens = req.MaxContextTokens.Value;
        if (req.TimeoutSeconds is > 0) model.TimeoutSeconds = req.TimeoutSeconds.Value;
        if (req.MaxRetries is >= 0) model.MaxRetries = req.MaxRetries.Value;
        if (req.Enabled is not null) model.Enabled = req.Enabled.Value;
        if (req.IsLocalOrPrivate is not null) model.IsLocalOrPrivate = req.IsLocalOrPrivate.Value;
        if (req.Provider is not null) model.Provider = req.Provider.Trim();
        if (req.Family is not null) model.Family = req.Family.Trim();
        // Tags 非空列表 = 整表替换（trim + 去空去重）；null = 不改。空列表允许清空。
        if (req.Tags is not null)
            model.Tags = req.Tags
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (req.InputPricePerMillion >= 0) model.InputPricePerMillion = req.InputPricePerMillion.Value;
        if (req.OutputPricePerMillion >= 0) model.OutputPricePerMillion = req.OutputPricePerMillion.Value;
        if (req.CachedInputPricePerMillion >= 0) model.CachedInputPricePerMillion = req.CachedInputPricePerMillion.Value;
        if (req.CacheWriteInputPricePerMillion >= 0) model.CacheWriteInputPricePerMillion = req.CacheWriteInputPricePerMillion.Value;

        // 自动命名（Name 留空）的模型被按显示名寻址更新时，物化该名字到文件：
        // UpsertModel 按非空 Name 匹配替换，留空会变成新增重复条目。
        if (string.IsNullOrWhiteSpace(model.Name))
            model.Name = names[idx];

        try
        {
            cfg.UpsertModel(model);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        return Results.Ok(new { message = $"Model '{name}' updated", model = new { model.Name, model.Id, model.BaseUrl, model.Tier, model.MaxContextTokens, model.TimeoutSeconds, model.MaxRetries, model.Enabled, model.InputPricePerMillion, model.OutputPricePerMillion, HasApiKey = !string.IsNullOrEmpty(model.ApiKey) } });
    }

    private static IResult DeleteModel(string name, ModelsConfigService cfg)
    {
        // 先按显示名解析出文件里的原始 Name（自动命名的模型文件内 Name 为空）。
        var models = cfg.LoadModels();
        var names = EffectiveNames(models);
        int idx = names.FindIndex(n => string.Equals(n, name, StringComparison.Ordinal));
        string rawName = idx >= 0 ? models[idx].Name : name;

        bool deleted = cfg.DeleteModel(rawName);
        if (!deleted)
            return Results.NotFound(new { error = $"Model '{name}' not found" });
        return Results.Ok(new { message = $"Model '{name}' deleted" });
    }

    private static async Task<IResult> TestEndpointConnectivity(
        string name, ModelsConfigService cfg, IModelClientProvider clientProvider)
    {
        var models = cfg.LoadModels();
        var names = EffectiveNames(models);
        var model = names
            .Select((n, i) => (n, i))
            .Where(t => string.Equals(t.n, name, StringComparison.Ordinal))
            .Select(t => models[t.i])
            .FirstOrDefault();
        if (model is null)
            return Results.NotFound(new { success = false, error = $"Model '{name}' not found" });

        var client = clientProvider.GetClient(model);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(10, model.TimeoutSeconds)));
        var result = await client.ProbeAsync(cts.Token).ConfigureAwait(false);
        return Results.Ok(new
        {
            success = result.Healthy,
            latencyMs = (long)result.LatencyMs,
            message = result.Healthy ? "连接正常 (OK)" : "连接异常",
            error = result.Error
        });
    }

    /// <summary>
    /// 计算模型列表的有效名称（与 /v1/models 及路由一致）：Name 非空用 Name；
    /// 留空（Id-only 配置，文件内不落名）时用归一化生成的 "{供应商}/{Id}"（重复追加 #N）。
    /// 供 GET 展示与 {name} 寻址复用；不改动文件内容。
    /// </summary>
    private static List<string> EffectiveNames(IList<ModelEndpointOptions> models)
    {
        // 复制后归一化，避免就地改写 LoadModels 返回的对象。
        var copies = models.Select(m => new ModelEndpointOptions
        {
            Name = m.Name,
            Id = m.Id,
            BaseUrl = m.BaseUrl,
            Provider = m.Provider,
            Family = m.Family,
            Tier = m.Tier
        }).ToList();
        ModelNameNormalizer.Normalize(copies);
        return copies.Select(m => m.Name).ToList();
    }

    private record UpdateModelRequest(
        string? BaseUrl,
        string? ApiKey,
        string? Tier,
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
        bool? IsLocalOrPrivate = null,
        List<string>? Tags = null);

    private record CreateModelRequest(
        string? Name,
        string BaseUrl,
        string? ApiKey,
        string? Tier,
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
