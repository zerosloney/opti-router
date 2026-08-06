using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// LLM token 估算器（纯静态，无 IO）。
/// </summary>
/// <remarks>
/// 经验系数：英文约 4 字符/token，中文约 1.5 字符/token（中文 token 密度高）。
/// 简化：按总字符数估算，混合内容用 3.5 字符/token 折中。
/// <para>
/// intentional-simple: 不引入真实 BPE tokenizer；粗估对路由分级足够，误差在 30% 内可接受。
/// 如需精确，可升级为接入 tiktoken 系或模型官方 tokenizer。
/// </para>
/// </remarks>
public static class TokenEstimator
{
    private const double CharsPerToken = 3.5;

    /// <summary>
    /// 估算请求的输入 token 数。
    /// </summary>
    public static int Estimate(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return 0;

        long totalChars = 0;
        foreach (var msg in request.Messages)
        {
            if (string.IsNullOrEmpty(msg.Content)) continue;
            totalChars += msg.Content.Length;
            // role 也算几个 token
            totalChars += msg.Role.Length;
        }

        // 3.5 字符/token，向上取整
        return (int)Math.Ceiling(totalChars / CharsPerToken);
    }
}
