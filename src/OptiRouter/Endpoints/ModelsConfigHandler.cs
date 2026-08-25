using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

            // 凭据复制：ApiKey 缺省时从既有模型（discover 批量导入的凭证源）在服务端复制，
            // 明文 key 不出服务端——前端不再需要 reveal 后回传。
            string? apiKey = req.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(req.ApiKeySourceModel))
            {
                apiKey = cfg.LoadModels()
                    .FirstOrDefault(m => string.Equals(m.Name, req.ApiKeySourceModel, StringComparison.Ordinal))?.ApiKey;
            }

            var model = new ModelEndpointOptions
            {
                Name = req.Name?.Trim() ?? string.Empty,
                Id = req.Id?.Trim() ?? string.Empty,
                BaseUrl = req.BaseUrl.Trim().TrimEnd('/'),
                ApiKey = apiKey,
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
                Weight = req.Weight is > 0 ? req.Weight : 1.0,
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
                return Results.Conflict(new { error = $"Model '{model.Name}' already exists. Use a different routing name and set 'id' to the upstream model id to add another account/key of the same model" });

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
        endpoints.MapGet("/api/models/{name}/apikey", (string name, ModelsConfigService cfg, HttpContext httpContext)
            => RevealApiKey(name, cfg, httpContext));
        endpoints.MapGet("/api/models/apikey", (string name, ModelsConfigService cfg, HttpContext httpContext)
            => RevealApiKey(name, cfg, httpContext));

        // 4c. POST discover — 管理员拉取上游可订阅模型列表（OpenAI 兼容 /v1/models、Gemini /v1beta/models）。
        //    复用现有已配置模型的 BaseUrl+ApiKey 也可手填；返回的 Id 由管理员在 UI 多选后批量创建。
        endpoints.MapPost("/api/models/discover", async (DiscoverRequest req, IHttpClientFactory httpFactory, ModelsConfigService cfg) =>
            await DiscoverUpstreamModels(req, httpFactory, cfg).ConfigureAwait(false));
    }

    /// <summary>
    /// 拉取上游 provider 的模型清单。Body：<c>{ baseUrl?, apiKey?, protocol?, modelName? }</c>。
    /// Anthropic 没有公开的 models 列表端点，直接 501。
    /// </summary>
    private static async Task<IResult> DiscoverUpstreamModels(DiscoverRequest req, IHttpClientFactory httpFactory, ModelsConfigService cfg)
    {
        if (req is null)
            return Results.BadRequest(new { error = "Request body is required" });

        string? baseUrl = req.BaseUrl;
        string? apiKey = req.ApiKey;
        string protocol = string.IsNullOrWhiteSpace(req.Protocol) ? "OpenAI" : req.Protocol!;

        if (!string.IsNullOrWhiteSpace(req.ModelName))
        {
            var models = cfg.LoadModels();
            var names = EffectiveNames(models);
            int idx = names.FindIndex(n => string.Equals(n, req.ModelName, StringComparison.Ordinal));
            if (idx >= 0)
            {
                var m = models[idx];
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = m.BaseUrl;
                if (string.IsNullOrWhiteSpace(apiKey)) apiKey = m.ApiKey;
                if (string.IsNullOrWhiteSpace(req.Protocol) || req.Protocol == "OpenAI")
                {
                    if (string.Equals(m.Provider, "gemini", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(m.BaseUrl) && m.BaseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase)))
                    {
                        protocol = "Gemini";
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return Results.BadRequest(new { error = "baseUrl is required" });

        if (string.Equals(protocol, "Anthropic", StringComparison.OrdinalIgnoreCase))
            return Results.Json(
                new { error = "Anthropic does not expose a public /v1/models endpoint; configure upstream model ids manually." },
                statusCode: StatusCodes.Status501NotImplemented);

        var client = httpFactory.CreateClient("model-discover");
        var candidates = string.Equals(protocol, "Gemini", StringComparison.OrdinalIgnoreCase)
            ? new[] { UpstreamModelsUrl.GeminiUrl(baseUrl) }
            : UpstreamModelsUrl.OpenAiCandidates(baseUrl);

        HttpResponseMessage resp;
        try
        {
            resp = await SendDiscoverAsync(client, candidates, apiKey, protocol);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new { error = $"Upstream unreachable: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Results.Json(
                new { error = $"Upstream returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {resp.RequestMessage?.RequestUri}", body = errBody },
                statusCode: StatusCodes.Status502BadGateway);
        }

        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        List<DiscoveredModel> items;
        try
        {
            items = ParseDiscoveredModels(text);
        }
        catch (JsonException ex)
        {
            return Results.Json(
                new { error = $"Invalid upstream response: {ex.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
        return Results.Ok(items);
    }

    /// <summary>按候选 URL 依次请求模型列表；404（路径猜错）回退下一候选，其余状态码（401/5xx 等）直接返回，避免掩盖真实错误与重复发送凭据。</summary>
    private static async Task<HttpResponseMessage> SendDiscoverAsync(
        HttpClient client, IReadOnlyList<string> candidateUrls, string? apiKey, string protocol)
    {
        HttpResponseMessage? resp = null;
        foreach (var url in candidateUrls)
        {
            resp?.Dispose();
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (string.Equals(protocol, "Gemini", StringComparison.OrdinalIgnoreCase))
                    msg.Headers.Add("x-goog-api-key", apiKey.Trim());
                else
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.NotFound)
                break;
        }

        return resp!;
    }

    /// <summary>解析 OpenAI 兼容 / Gemini / Ollama 等上游模型列表响应，未知字段一并透传。</summary>
    private static List<DiscoveredModel> ParseDiscoveredModels(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var items = new List<DiscoveredModel>();

        JsonElement? listEl = null;
        if (root.ValueKind == JsonValueKind.Array)
        {
            listEl = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                listEl = dataEl;
            else if (root.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
                listEl = modelsEl;
            else if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                listEl = itemsEl;
        }

        if (listEl.HasValue)
        {
            foreach (var entry in listEl.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var s = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        items.Add(new DiscoveredModel(s, s, null, null));
                    continue;
                }
                if (entry.ValueKind != JsonValueKind.Object) continue;

                string? id = null;
                if (entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString();
                else if (entry.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    id = nameProp.GetString();
                else if (entry.TryGetProperty("model", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                    id = mProp.GetString();

                if (string.IsNullOrWhiteSpace(id)) continue;

                // 规范化 Gemini "models/gemini-1.5-pro" -> id 为 "gemini-1.5-pro" 或原始 id
                string cleanId = id.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? id.Substring("models/".Length) : id;
                string displayName = cleanId;
                if (entry.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dnEl.GetString()))
                    displayName = dnEl.GetString()!;

                string? ownedBy = null;
                if (entry.TryGetProperty("owned_by", out var obEl) && obEl.ValueKind == JsonValueKind.String)
                    ownedBy = obEl.GetString();
                else if (entry.TryGetProperty("ownedBy", out var obEl2) && obEl2.ValueKind == JsonValueKind.String)
                    ownedBy = obEl2.GetString();

                items.Add(new DiscoveredModel(cleanId, displayName, ownedBy, entry.Clone()));
            }
        }
        return items;
    }

    private static IResult RevealApiKey(string name, ModelsConfigService cfg, HttpContext httpContext)
    {
        // 明文密钥响应禁止任何缓存（浏览器返回键/中间代理留存），仅本次会话可见。
        httpContext.Response.Headers.CacheControl = "no-store";
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
        if (req.Weight is not null && req.Weight >= 0) model.Weight = req.Weight.Value;
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
        // 探活回答（"你是什么模型"）截断后随 message 回显，管理员可直接核对模型身份。
        string reply = result.Reply?.Trim() ?? "";
        if (reply.Length > 80) reply = reply[..80] + "…";
        return Results.Ok(new
        {
            success = result.Healthy,
            latencyMs = (long)result.LatencyMs,
            message = !result.Healthy
                ? "连接异常"
                : reply.Length > 0 ? $"连接正常 (OK) · 回答: {reply}" : "连接正常 (OK)",
            error = SanitizeProbeError(result.Error)
        });
    }

    /// <summary>
    /// 探活失败原因压平截断后回显管理台：上游异常整段堆栈/连接细节不透出。
    /// </summary>
    private static string? SanitizeProbeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return error;
        string flat = string.Join(' ', error.Split('\n')).Trim();
        return flat.Length <= 300 ? flat : flat[..300] + "…";
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
        List<string>? Tags = null,
        double? Weight = null);

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
        bool? IsLocalOrPrivate = null,
        double Weight = 1.0,
        string? ApiKeySourceModel = null);
}

/// <summary>上游 provider 模型拉取请求。ApiKey 可空，开源自托管多不要求鉴权；ModelName 指定时自动复用已配置模型的凭据与端点。</summary>
public record DiscoverRequest(string? BaseUrl = null, string? ApiKey = null, string? Protocol = "OpenAI", string? ModelName = null);

/// <summary>上游 provider 模型拉取结果。Raw 透传原 JSON 节点，供 UI 扩展展示（如 context_length 等非标字段）。</summary>
public record DiscoveredModel(string Id, string? Name, string? OwnedBy, JsonElement? Raw);
