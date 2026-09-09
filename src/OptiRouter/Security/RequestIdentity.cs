using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace OptiRouter.Security;

internal static class RequestIdentity
{
    internal static string? ResolveClientIp(HttpContext context, bool trustProxyHeaders)
    {
        var headers = context.Request.Headers;
        if (trustProxyHeaders && headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
            return cfIp.ToString();
        if (trustProxyHeaders && headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
        {
            var chain = xff.ToString().AsSpan();
            int separator = chain.IndexOf(',');
            return (separator < 0 ? chain : chain[..separator]).Trim().ToString();
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }

    internal static string ResolvePartitionKey(HttpContext context, bool trustProxyHeaders)
    {
        string? ip = ResolveClientIp(context, trustProxyHeaders);
        if (!string.IsNullOrEmpty(ip))
            return $"ip:{ip}";

        // Preserve the partition contract separately from protocol authentication:
        // native API keys and X-Session-Id have never been partition identities.
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader)
            && authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string token = authHeader.ToString().Substring("Bearer ".Length).Trim();
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return $"auth:{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
        }
        return "anonymous";
    }

    internal static string? ExtractApiKey(HttpContext context)
    {
        if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization)
            && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            // A parsed Bearer header, even without a token, suppresses native-key fallback.
            return authorization.Parameter;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/v1/messages"))
            return context.Request.Headers["x-api-key"].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (path.StartsWithSegments("/v1beta"))
        {
            return context.Request.Headers["x-goog-api-key"].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                ?? context.Request.Query["key"].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
        return null;
    }
}
