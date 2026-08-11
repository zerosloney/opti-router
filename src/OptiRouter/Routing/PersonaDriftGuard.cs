using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 人设一致性与 Persona 漂移防护引擎：
/// 在多轮 Agent 对话或切换模型/融合路由 Outer 阶段，自动植入静态人设锚点提示词，
/// 锁定连贯的说话语气、性格偏好与语言习惯，防止模型在多轮交互中产生 Persona 漂移。
/// </summary>
public static class PersonaDriftGuard
{
    public const string DefaultPersonaAnchorInstruction =
        "【人设与风格一致性指示】：请保持与本会话前文完全一致的人设角色、专业语气（如严谨客密/亲和幽默）、语言风格及 Markdown 排版习惯，继续进行后续回答。不要突变性格或回答格式。";

    /// <summary>
    /// 为 ChatRequest 注入人设锚点提示词。
    /// </summary>
    public static ChatRequest ApplyPersonaAnchor(ChatRequest request, string? customAnchorPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null || request.Messages.Count == 0)
            return request;

        string anchorPrompt = string.IsNullOrWhiteSpace(customAnchorPrompt)
            ? DefaultPersonaAnchorInstruction
            : customAnchorPrompt;

        var messages = new List<ChatMessage>(request.Messages.Count + 1);

        // 如果原消息链中已有 system 消息，追加到 system 消息尾部；否则在最前插入一条 system 人设锚点
        bool injectedInSystem = false;
        foreach (var msg in request.Messages)
        {
            if (!injectedInSystem && msg is not null && msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                string existingText = msg.GetText();
                string updatedText = $"{existingText}\n\n{anchorPrompt}".Trim();
                messages.Add(ChatMessage.FromText("system", updatedText));
                injectedInSystem = true;
            }
            else if (msg is not null)
            {
                messages.Add(msg);
            }
        }

        if (!injectedInSystem)
        {
            messages.Insert(0, ChatMessage.FromText("system", anchorPrompt));
        }

        return request with { Messages = messages };
    }
}
