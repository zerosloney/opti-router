// 策略横评的冒烟用 mock 上游：OpenAI 兼容 /v1/chat/completions（仅非流式，评测路径不走流式）。
// intentional-simple: 固定话术 + 按模型档位区分延迟/长度/用量，只为验证 harness 机制
// （路由选型、Fusion 编排、计费与报告管线），不冒充真实质量数据。真实横评直接对真上游跑。
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 三档行为画像：延迟（模拟 TTFT+生成）、回答长度、token 用量全部拉开差距，
// 让延迟/成本/选型指标在横评汇总里可见。
app.MapPost("/v1/chat/completions", async (HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    string model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "mock" : "mock";
    string question = "";
    bool wantsJson = false;
    if (root.TryGetProperty("response_format", out var rf)) { wantsJson = true; }
    if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
    {
        foreach (var msg in msgs.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out var r) && r.GetString() == "user"
                && msg.TryGetProperty("content", out var c))
            {
                question = c.GetString() ?? "";
                if (question.Contains("consensus")) { wantsJson = true; }
                // 不 break：取最后一条 user（与路由器取末轮 user 的口径一致）
            }
        }
    }

    // Fusion analyst 的结构化契约：mock 按契约回 JSON，让融合路径能走通到 outer
    if (wantsJson)
    {
        object analysis = new
        {
            consensus = $"[{model}] 各模型对「{Truncate(question, 40)}」的模拟结论一致。",
            contradictions = "",
            gaps = "",
            unique_insights = "",
            recommendation = "采纳多数派结论。"
        };
        int aPrompt = Math.Max(1, question.Length / 2);
        return Results.Json(new
        {
            id = $"chatcmpl-mock-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = System.Text.Json.JsonSerializer.Serialize(analysis) }, finish_reason = "stop" } },
            usage = new { prompt_tokens = aPrompt, completion_tokens = 80, total_tokens = aPrompt + 80 }
        });
    }

    // 首 token 延迟 + 生成延迟按档位区分（Strong 慢而长，Cheap 快而短）
    int delayMs = model switch
    {
        var s when s.Contains("strong") => 900,
        var s when s.Contains("medium") => 350,
        _ => 120
    };
    await Task.Delay(delayMs);

    int answerChars = model switch
    {
        var s when s.Contains("strong") => 900,
        var s when s.Contains("medium") => 420,
        _ => 140
    };
    string answer = $"[{model}] 这是针对「{Truncate(question, 60)}」的模拟回答。" + new string('答', answerChars);

    int promptTokens = Math.Max(1, question.Length / 2);
    int completionTokens = answer.Length / 2;

    object response = new
    {
        id = $"chatcmpl-mock-{Guid.NewGuid():N}",
        @object = "chat.completion",
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        model,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content = answer },
                finish_reason = "stop"
            }
        },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens }
    };
    return Results.Json(response);
});

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

app.Run("http://127.0.0.1:5199");
