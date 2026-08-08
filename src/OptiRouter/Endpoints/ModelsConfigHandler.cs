using OptiRouter.Configuration;

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
        // 1. Models Config UI
        endpoints.MapGet("/models", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetHtmlContent()).ConfigureAwait(false);
        });

        // 2. GET all (不暴露完整 ApiKey，只返回是否已配置)
        endpoints.MapGet("/api/models", (ModelsConfigService cfg) =>
        {
            var models = cfg.LoadModels().Select(m => new
            {
                m.Name,
                m.BaseUrl,
                m.Tier,
                m.MaxContextTokens,
                m.TimeoutSeconds,
                m.MaxRetries,
                m.Enabled,
                m.InputPricePerMillion,
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
                m.Name, m.BaseUrl, m.Tier, m.MaxContextTokens, m.TimeoutSeconds,
                m.MaxRetries, m.Enabled, m.InputPricePerMillion, m.OutputPricePerMillion, m.Tags,
                HasApiKey = !string.IsNullOrEmpty(m.ApiKey)
            });
            return Results.Json(new { models, configFile = cfg.ConfigFilePath });
        });

        // 4. POST create new model
        endpoints.MapPost("/api/models", (ModelsConfigService cfg, CreateModelRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Model name is required" });
            if (string.IsNullOrWhiteSpace(req.BaseUrl))
                return Results.BadRequest(new { error = "BaseUrl is required" });

            var models = cfg.LoadModels();
            if (models.Any(m => string.Equals(m.Name, req.Name, StringComparison.Ordinal)))
                return Results.Conflict(new { error = $"Model '{req.Name}' already exists" });

            var model = new ModelEndpointOptions
            {
                Name = req.Name.Trim(),
                BaseUrl = req.BaseUrl.Trim().TrimEnd('/'),
                ApiKey = req.ApiKey,
                Tier = req.Tier ?? ModelTier.Medium,
                // 数值 clamp：镜像 RouterOptionsValidator 边界，防止坏值落盘导致重启 ValidateOnStart 失败。
                MaxContextTokens = (req.MaxContextTokens is > 0) ? req.MaxContextTokens.Value : 8192,
                InputPricePerMillion = (req.InputPricePerMillion ?? 0) < 0 ? 0 : req.InputPricePerMillion!.Value,
                OutputPricePerMillion = (req.OutputPricePerMillion ?? 0) < 0 ? 0 : req.OutputPricePerMillion!.Value,
                TimeoutSeconds = (req.TimeoutSeconds is > 0) ? req.TimeoutSeconds.Value : 120,
                MaxRetries = (req.MaxRetries is >= 0) ? req.MaxRetries.Value : 0,
                Enabled = req.Enabled ?? true
            };
            if (req.Tags is not null)
                foreach (var tag in req.Tags)
                    model.Tags.Add(tag);

            try
            {
                cfg.UpsertModel(model);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Created($"/api/models/{model.Name}", new { message = $"Model '{model.Name}' created", model = new { model.Name, model.BaseUrl, model.Tier, model.Enabled } });
        });

        // 5. PUT update single model (持久化到文件 + 热重载)
        endpoints.MapPut("/api/models/{name}", (string name, ModelsConfigService cfg, UpdateModelRequest req) =>
        {
            var models = cfg.LoadModels();
            var model = models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (model is null)
                return Results.NotFound(new { error = $"Model '{name}' not found" });

            if (req.BaseUrl is not null && !string.IsNullOrWhiteSpace(req.BaseUrl)) model.BaseUrl = req.BaseUrl.TrimEnd('/');
            if (req.ApiKey is not null) model.ApiKey = req.ApiKey; // 空字符串表示清除
            if (req.Tier is not null) model.Tier = req.Tier.Value;
            if (req.MaxContextTokens is > 0) model.MaxContextTokens = req.MaxContextTokens.Value;
            if (req.TimeoutSeconds is > 0) model.TimeoutSeconds = req.TimeoutSeconds.Value;
            if (req.MaxRetries is >= 0) model.MaxRetries = req.MaxRetries.Value;
            if (req.Enabled is not null) model.Enabled = req.Enabled.Value;
            if (req.InputPricePerMillion >= 0) model.InputPricePerMillion = req.InputPricePerMillion.Value;
            if (req.OutputPricePerMillion >= 0) model.OutputPricePerMillion = req.OutputPricePerMillion.Value;

            try
            {
                cfg.UpsertModel(model);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Ok(new { message = $"Model '{name}' updated", model = new { model.Name, model.BaseUrl, model.Tier, model.MaxContextTokens, model.TimeoutSeconds, model.MaxRetries, model.Enabled, model.InputPricePerMillion, model.OutputPricePerMillion, HasApiKey = !string.IsNullOrEmpty(model.ApiKey) } });
        });

        // 6. DELETE remove model
        endpoints.MapDelete("/api/models/{name}", (string name, ModelsConfigService cfg) =>
        {
            bool deleted = cfg.DeleteModel(name);
            if (!deleted)
                return Results.NotFound(new { error = $"Model '{name}' not found" });
            return Results.Ok(new { message = $"Model '{name}' deleted" });
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
        decimal? OutputPricePerMillion);

    private record CreateModelRequest(
        string Name,
        string BaseUrl,
        string? ApiKey,
        ModelTier? Tier,
        int? MaxContextTokens,
        int? TimeoutSeconds,
        int? MaxRetries,
        bool? Enabled,
        decimal? InputPricePerMillion,
        decimal? OutputPricePerMillion,
        List<string>? Tags);

    private static string GetHtmlContent()
    {
        return string.Join("\n", new[]
        {
            @"<!DOCTYPE html>",
            @"<html lang=""zh"">",
            @"<head>",
            @"    <meta charset=""UTF-8"">",
            @"    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">",
            @"    <title>OptiRouter - 模型配置</title>",
            @"    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap"" rel=""stylesheet"">",
            @"    <style>",
            @"        :root {",
            @"            --bg-base: #090d16;",
            @"            --bg-surface: rgba(16, 22, 35, 0.65);",
            @"            --bg-card: rgba(22, 30, 49, 0.8);",
            @"            --text-primary: #f3f4f6;",
            @"            --text-secondary: #9ca3af;",
            @"            --primary: #6366f1;",
            @"            --primary-glow: rgba(99, 102, 241, 0.15);",
            @"            --success: #10b981;",
            @"            --success-glow: rgba(16, 185, 129, 0.15);",
            @"            --warning: #f59e0b;",
            @"            --danger: #ef4444;",
            @"            --danger-glow: rgba(239, 68, 68, 0.15);",
            @"            --border: rgba(255, 255, 255, 0.06);",
            @"            --border-hover: rgba(255, 255, 255, 0.12);",
            @"        }",
            @"        * { box-sizing: border-box; margin: 0; padding: 0; }",
            @"        body {",
            @"            font-family: 'Outfit', -apple-system, BlinkMacSystemFont, sans-serif;",
            @"            background-color: var(--bg-base);",
            @"            color: var(--text-primary);",
            @"            min-height: 100vh;",
            @"            background-image:",
            @"                radial-gradient(circle at 10% 20%, rgba(99, 102, 241, 0.05) 0%, transparent 40%),",
            @"                radial-gradient(circle at 90% 80%, rgba(16, 185, 129, 0.03) 0%, transparent 40%);",
            @"        }",
            @"        header {",
            @"            display: flex; justify-content: space-between; align-items: center;",
            @"            padding: 1.5rem 2rem; border-bottom: 1px solid var(--border);",
            @"            backdrop-filter: blur(12px); position: sticky; top: 0; z-index: 100;",
            @"            background: rgba(9, 13, 22, 0.8);",
            @"        }",
            @"        .logo-area { display: flex; align-items: center; gap: 0.75rem; }",
            @"        .logo-icon {",
            @"            width: 2.2rem; height: 2.2rem;",
            @"            background: linear-gradient(135deg, var(--primary), #8b5cf6);",
            @"            border-radius: 0.5rem; display: flex; align-items: center; justify-content: center;",
            @"            box-shadow: 0 0 15px rgba(99, 102, 241, 0.4);",
            @"            font-weight: 700; font-size: 1.2rem;",
            @"        }",
            @"        .logo-title {",
            @"            font-size: 1.4rem; font-weight: 700; letter-spacing: -0.025em;",
            @"            background: linear-gradient(to right, #ffffff, #c7d2fe);",
            @"            -webkit-background-clip: text; -webkit-text-fill-color: transparent;",
            @"        }",
            @"        .nav { display: flex; align-items: center; gap: 1.25rem; }",
            @"        .nav-link {",
            @"            color: var(--text-secondary); text-decoration: none; font-size: 0.9rem; font-weight: 500;",
            @"            padding: 0.4rem 0.8rem; border-radius: 0.375rem; transition: all 0.2s;",
            @"        }",
            @"        .nav-link:hover { color: var(--text-primary); background: rgba(255,255,255,0.04); }",
            @"        .nav-link.active { color: #fff; background: var(--primary-glow); border: 1px solid rgba(99,102,241,0.3); }",
            @"        main { max-width: 1400px; margin: 0 auto; padding: 2rem; }",
            @"        .glass-card {",
            @"            background: var(--bg-card); border: 1px solid var(--border);",
            @"            border-radius: 0.75rem; padding: 1.5rem;",
            @"            backdrop-filter: blur(16px);",
            @"            box-shadow: 0 8px 32px 0 rgba(0, 0, 0, 0.3);",
            @"        }",
            @"        .section-title {",
            @"            font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.05em;",
            @"            color: var(--text-secondary); font-weight: 600;",
            @"        }",
            @"        .banner-alert {",
            @"            background: var(--primary-glow); border: 1px solid rgba(99, 102, 241, 0.2);",
            @"            padding: 1rem; border-radius: 0.75rem; font-size: 0.9rem;",
            @"            margin-bottom: 1.5rem; display: flex; align-items: center; gap: 0.75rem;",
            @"        }",
            @"        .indicator { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }",
            @"        .pulse-active { animation: pulse 2s infinite; }",
            @"        @keyframes pulse { 0% { transform: scale(0.9); opacity: 0.6; } 50% { transform: scale(1.1); opacity: 1; } 100% { transform: scale(0.9); opacity: 0.6; } }",
            @"        .refresh-btn {",
            @"            background: var(--primary); color: #fff; border: none;",
            @"            padding: 0.5rem 1rem; border-radius: 0.375rem;",
            @"            font-weight: 500; cursor: pointer; font-family: inherit; font-size: 0.85rem; transition: filter 0.2s;",
            @"        }",
            @"        .refresh-btn:hover { filter: brightness(1.15); }",
            @"        .config-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }",
            @"        .config-table th {",
            @"            text-align: left; padding: 0.6rem 0.75rem;",
            @"            color: var(--text-secondary); font-weight: 600;",
            @"            border-bottom: 1px solid var(--border);",
            @"            font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em;",
            @"        }",
            @"        .config-table td { padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--border); }",
            @"        .config-table input, .config-table select {",
            @"            background: rgba(255, 255, 255, 0.03); color: var(--text-primary);",
            @"            border: 1px solid var(--border); padding: 0.3rem 0.5rem;",
            @"            border-radius: 0.25rem; font-family: 'JetBrains Mono', monospace; font-size: 0.8rem; width: 100%;",
            @"        }",
            @"        .config-table input:focus, .config-table select:focus { outline: none; border-color: var(--primary); }",
            @"        .action-btn {",
            @"            background: var(--primary); color: #fff; border: none;",
            @"            padding: 0.3rem 0.6rem; border-radius: 0.3rem; cursor: pointer;",
            @"            font-family: inherit; font-size: 0.75rem; margin-right: 3px;",
            @"        }",
            @"        .action-btn:hover { filter: brightness(1.15); }",
            @"        .del-btn {",
            @"            background: var(--danger); color: #fff; border: none;",
            @"            padding: 0.3rem 0.6rem; border-radius: 0.3rem; cursor: pointer;",
            @"            font-family: inherit; font-size: 0.75rem;",
            @"        }",
            @"        .del-btn:hover { filter: brightness(1.15); }",
            @"        .config-toast {",
            @"            position: fixed; bottom: 2rem; right: 2rem;",
            @"            padding: 0.75rem 1.25rem; border-radius: 0.5rem;",
            @"            font-size: 0.85rem; font-weight: 500;",
            @"            z-index: 300; display: none;",
            @"        }",
            @"        .config-toast.success { display: block; background: var(--success-glow); border: 1px solid rgba(16, 185, 129, 0.3); color: var(--success); }",
            @"        .config-toast.error { display: block; background: var(--danger-glow); border: 1px solid rgba(239, 68, 68, 0.3); color: var(--danger); }",
            @"    </style>",
            @"</head>",
            @"<body>",
            @"    <header>",
            @"        <div class=""logo-area"">",
            @"            <div class=""logo-icon"">&#937;</div>",
            @"            <div class=""logo-title"">OptiRouter</div>",
            @"        </div>",
            @"        <nav class=""nav"">",
            @"            <a class=""nav-link"" id=""nav-dashboard"" href=""/dashboard"">监控</a>",
            @"            <a class=""nav-link active"" id=""nav-models"" href=""/models"">模型配置</a>",
            @"        </nav>",
            @"    </header>",
            @"    <main>",
            @"        <div class=""banner-alert"">",
            @"            <span class=""indicator pulse-active"" style=""background: var(--primary);""></span>",
            @"            <span>模型配置页。增删改后立即热生效（写入 models-config.json 并触发配置重载）。</span>",
            @"        </div>",
            @"        <div class=""glass-card"">",
            @"            <div style=""display:flex; justify-content:space-between; align-items:center; margin-bottom:1rem;"">",
            @"                <div class=""section-title"">模型配置</div>",
            @"                <button class=""refresh-btn"" id=""toggle-add-form-btn"" onclick=""toggleAddForm()"">+ 添加模型</button>",
            @"            </div>",
            @"            <div id=""add-model-form"" style=""display:none; background:var(--bg-surface); border:1px solid var(--border); border-radius:0.5rem; padding:1.25rem; margin-bottom:1rem;"">",
            @"                <div style=""font-size:0.85rem; font-weight:600; margin-bottom:1rem; color:var(--text-primary);"">新增模型</div>",
            @"                <div style=""display:grid; grid-template-columns:1fr 1fr; gap:0.6rem;"">",
            @"                    <div style=""grid-column:1/-1;""><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">名称 *</label><input type=""text"" id=""add-name"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;"" placeholder=""如 gpt-4o""></div>",
            @"                    <div style=""grid-column:1/-1;""><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">BaseUrl *</label><input type=""text"" id=""add-baseurl"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;"" placeholder=""https://api.openai.com/v1""></div>",
            @"                    <div style=""grid-column:1/-1;""><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">ApiKey</label><input type=""password"" id=""add-apikey"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;"" placeholder=""sk-...""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">Tier</label><select id=""add-tier"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""><option value=""Strong"">Strong</option><option value=""Medium"" selected>Medium</option><option value=""Cheap"">Cheap</option></select></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">最大上下文 Token</label><input type=""number"" id=""add-ctx"" value=""8192"" min=""1"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">超时（秒）</label><input type=""number"" id=""add-timeout"" value=""120"" min=""1"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">最大重试次数</label><input type=""number"" id=""add-retry"" value=""0"" min=""0"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">输入价格 $/M</label><input type=""number"" id=""add-inp"" value=""0"" min=""0"" step=""0.001"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">输出价格 $/M</label><input type=""number"" id=""add-out"" value=""0"" min=""0"" step=""0.001"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""></div>",
            @"                    <div><label style=""font-size:0.75rem; color:var(--text-secondary); display:block; margin-bottom:0.2rem;"">启用</label><select id=""add-enabled"" style=""width:100%; background:rgba(255,255,255,0.03); color:var(--text-primary); border:1px solid var(--border); padding:0.4rem 0.6rem; border-radius:0.3rem; font-family:inherit; font-size:0.8rem; box-sizing:border-box;""><option value=""true"" selected>是</option><option value=""false"">否</option></select></div>",
            @"                </div>",
            @"                <div id=""add-form-error"" style=""color:var(--danger); font-size:0.8rem; margin-top:0.6rem; display:none;""></div>",
            @"                <div style=""display:flex; gap:0.6rem; margin-top:1rem; justify-content:flex-end;"">",
            @"                    <button onclick=""toggleAddForm()"" style=""background:var(--bg-surface); color:var(--text-secondary); border:1px solid var(--border); padding:0.4rem 0.9rem; border-radius:0.3rem; cursor:pointer; font-family:inherit; font-size:0.8rem;"">取消</button>",
            @"                    <button onclick=""submitAddModel()"" style=""background:var(--primary); color:#fff; border:none; padding:0.4rem 1rem; border-radius:0.3rem; cursor:pointer; font-family:inherit; font-size:0.8rem; font-weight:500;"">确认添加</button>",
            @"                </div>",
            @"            </div>",
            @"            <div style=""overflow-x:auto;"">",
            @"                <table class=""config-table"">",
            @"                    <thead><tr><th>名称</th><th>BaseUrl</th><th>ApiKey</th><th>Tier</th><th>上下文</th><th>超时(秒)</th><th>重试</th><th>启用</th><th>输入$/M</th><th>输出$/M</th><th>操作</th></tr></thead>",
            @"                    <tbody id=""config-body""></tbody>",
            @"                </table>",
            @"            </div>",
            @"        </div>",
            @"    </main>",
            @"    <div class=""config-toast"" id=""config-toast""></div>",
            @"    <script src=""/models.js""></script>",
            @"</body>",
            @"</html>"
        });
    }
}
