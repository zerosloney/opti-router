namespace OptiRouter.Clients;

/// <summary>
/// 上游模型列表（/models）探测 URL 候选构建。
/// OpenAI 兼容生态的 base url 有两种填法：已含版本段（…/v1、…/plan/v3，端点 = base + /models）
/// 与未含版本段（仅主机名，端点 = base + /v1/models）。字符串本身无法区分归属，
/// 故按启发式产出有序候选，由调用方对 404 依次回退（用验证代替猜测）。
/// </summary>
internal static class UpstreamModelsUrl
{
    /// <summary>OpenAI 兼容候选：以 /v1 结尾约定明确、无需回退；否则首选补 /v1，404 回退 base + /models。</summary>
    public static IReadOnlyList<string> OpenAiCandidates(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? [$"{trimmed}/models"]
            : [$"{trimmed}/v1/models", $"{trimmed}/models"];
    }

    /// <summary>Gemini 官方端点固定为 base + /v1beta/models，单候选。</summary>
    public static string GeminiUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/v1beta/models";
}
