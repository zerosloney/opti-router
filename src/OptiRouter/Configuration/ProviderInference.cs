namespace OptiRouter.Configuration;

/// <summary>
/// 从模型端点 <see cref="ModelEndpointOptions.BaseUrl"/> 推断供应商标识。
/// 仅用于 /v1/models 等展示层；不影响路由决策（软多样性仍用显式 <c>Provider</c> 配置）。
/// </summary>
public static class ProviderInference
{
    // host 后缀 → 供应商标识。后缀匹配，覆盖 api./www. 等子域前缀差异。
    private static readonly (string HostSuffix, string Provider)[] KnownHosts =
    {
        ("deepseek.com", "deepseek"),
        ("openai.com", "openai"),
        ("anthropic.com", "anthropic"),
        ("claude.ai", "anthropic"),
        ("azure.com", "azure"),
        ("azure-api.net", "azure"),
        ("googleapis.com", "google"),
        ("groq.com", "groq"),
        ("mistral.ai", "mistral"),
        ("x.ai", "xai"),
        ("cohere.com", "cohere"),
        ("moonshot.cn", "moonshot"),
        ("moonshot.ai", "moonshot"),
        ("bigmodel.cn", "zhipu"),
        ("openrouter.ai", "openrouter"),
        ("together.ai", "together"),
        ("together.xyz", "together"),
        ("fireworks.ai", "fireworks"),
        ("siliconflow.cn", "siliconflow"),
        ("deepinfra.com", "deepinfra"),
        ("aliyuncs.com", "aliyun"),
        ("volces.com", "volcengine"),
        ("baichuan-ai.com", "baichuan"),
        ("tencentcloudapi.com", "tencent"),
        ("01.ai", "lingyi"),
        ("stepfun.com", "stepfun"),
        ("stepfun.ai", "stepfun"),
        ("minimaxi.com", "minimax"),
        ("minimax.chat", "minimax"),
        ("amazonaws.com", "aws"),
        ("cloudflare.com", "cloudflare")
    };

    /// <summary>
    /// 推断供应商标识。本机/内网地址返回 "local"；已知云厂商按 host 后缀匹配；
    /// 其余取主域（如 llm.mycompany.com → mycompany.com）；无法解析返回空串。
    /// </summary>
    /// <param name="baseUrl">模型端点 BaseUrl。</param>
    /// <returns>供应商标识；未知为空串，由调用方决定兜底显示。</returns>
    public static string Infer(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return string.Empty;
        }

        var host = uri.Host.ToLowerInvariant();
        if (IsLocalHost(host))
        {
            return "local";
        }

        foreach (var (suffix, provider) in KnownHosts)
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        // 通用回退：取主域（最后两个 label）作标识，去掉 api./www./llm. 等子域噪声。
        var labels = host.Split('.');
        return labels.Length >= 2
            ? $"{labels[^2]}.{labels[^1]}"
            : host;
    }

    private static bool IsLocalHost(string host)
    {
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]")
        {
            return true;
        }

        // 内网段：10.x / 192.168.x / 172.16-31.x
        var parts = host.Split('.');
        if (parts.Length == 4 &&
            byte.TryParse(parts[0], out var a) &&
            byte.TryParse(parts[1], out var b) &&
            byte.TryParse(parts[2], out _) &&
            byte.TryParse(parts[3], out _))
        {
            return a == 10 ||
                   (a == 192 && b == 168) ||
                   (a == 172 && b >= 16 && b <= 31);
        }

        return false;
    }
}
