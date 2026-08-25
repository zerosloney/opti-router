namespace OptiRouter.Clients;

/// <summary>
/// 上游模型列表（/models）探测 URL 候选构建。
/// OpenAI 兼容生态的 base url 有三种布局：未含版本段（端点 = base + /v1/models）、
/// 已含版本段（…/v1、…/plan/v3，端点 = base + /models）、业务端点在路径前缀下而
/// 模型列表挂在站点根（腾讯 TokenHub Token Plan：chat 在 /plan/v3，列表仅在根 /v1/models）。
/// 字符串本身无法区分归属，故按启发式产出有序候选，由调用方对 404 依次回退（用验证代替猜测）。
/// </summary>
internal static class UpstreamModelsUrl
{
    /// <summary>OpenAI 兼容候选：以 /v1 结尾约定明确；否则首选补 /v1、再回退 base + /models；base 含路径时追加站点根 /v1/models 终选兜底（去重）。</summary>
    public static IReadOnlyList<string> OpenAiCandidates(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        List<string> candidates = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? [$"{trimmed}/models"]
            : [$"{trimmed}/v1/models", $"{trimmed}/models"];

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.AbsolutePath.TrimEnd('/')))
        {
            var root = $"{uri.GetLeftPart(UriPartial.Authority)}/v1/models";
            if (!candidates.Contains(root, StringComparer.OrdinalIgnoreCase))
                candidates.Add(root);
        }

        return candidates;
    }

    /// <summary>Gemini 官方端点固定为 base + /v1beta/models，单候选。</summary>
    public static string GeminiUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/v1beta/models";
}
