using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate RouterOptions on startup.
builder.Services.AddOptions<RouterOptions>()
    .Bind(builder.Configuration.GetSection("OptiRouter"))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<RouterOptions>, RouterOptionsValidator>();

// 注册模型客户端工厂。
builder.Services.AddSingleton<ModelClientFactory>();

// 注册模型客户端提供者（生产实现，按模型名缓存 IModelClient）。
builder.Services.AddSingleton<IModelClientProvider, ModelClientProvider>();

// t3: 注册成本账本和路由引擎。
builder.Services.AddSingleton<CostLedger>();
builder.Services.AddSingleton<RouterEngine>(sp =>
{
    var ledger = sp.GetRequiredService<CostLedger>();
    var options = sp.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue;
    var routing = options.Routing;
    var policies = new List<IRouterPolicy>();

    if (routing.EnableRuleClassifier)
        policies.Add(new RuleClassifierPolicy());

    if (routing.EnableTokenEstimator)
        policies.Add(new LongInputPolicy());

    if (routing.EnableBudgetGuard)
        policies.Add(new BudgetGuardPolicy(ledger));

    if (routing.EnableFailover)
        policies.Add(new FailoverPolicy());

    return new RouterEngine(ledger, policies);
});

// t4: 注册降级重试编排器。
builder.Services.AddSingleton<ProxyOrchestrator>();

var app = builder.Build();

app.MapGet("/health", () => "ok");

// t4: 暴露 OpenAI 兼容 Chat Completions 端点。
app.MapChatCompletions();

app.Run();
