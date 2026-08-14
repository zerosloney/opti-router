using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 响应缓存键：对影响生成结果的请求字段做稳定规范化后取 SHA256（hex）。
/// </summary>
/// <remarks>
/// 含 model / messages / temperature / max_tokens + 经 <see cref="ChatRequest.ExtensionData"/> 透传的生成参数
///（top_p / seed / n / stop / response_format 等）。忽略 stream（仅非流式才缓存）。
/// <see cref="ChatRequest.ExtensionData"/> 按 key 排序以保证键稳定（Dictionary 顺序不保证）。
/// 必须在 PII 脱敏<strong>之前</strong>调用：用原始请求算键，避免不同 PII 脱敏后占位符相同导致缓存串扰。
/// </remarks>
public static class ResponseCacheKey
{
    /// <summary>计算请求的缓存键（小写 hex SHA256）。</summary>
    public static string Compute(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sb = new StringBuilder();
        sb.Append("m=").Append(request.Model ?? string.Empty).Append('\n');
        sb.Append("t=").Append(request.Temperature is { } t ? t.ToString("R", CultureInfo.InvariantCulture) : "null").Append('\n');
        sb.Append("x=").Append(request.MaxTokens is { } x ? x.ToString(CultureInfo.InvariantCulture) : "null").Append('\n');

        if (request.Messages is { Count: > 0 } messages)
        {
            foreach (var msg in messages)
            {
                if (msg is null) continue;
                sb.Append("r=").Append(msg.Role ?? string.Empty).Append('\n');
                sb.Append("c=").Append(msg.Content is { } el ? el.GetRawText() : "null").Append('\n');
            }
        }

        if (request.ExtensionData is { Count: > 0 } ext)
        {
            foreach (var kv in ext.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value.GetRawText()).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
