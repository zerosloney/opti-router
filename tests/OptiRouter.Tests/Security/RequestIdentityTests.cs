using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OptiRouter.Configuration;
using OptiRouter.Security;
using Xunit.Abstractions;

namespace OptiRouter.Tests.Security;

public sealed class RequestIdentityTests(ITestOutputHelper output)
{
    [Fact]
    public void IpAndPartitionResolution_MatchLegacyForHeaderMatrix()
    {
        string?[] forwarded = [null, "", " ", ",203.0.113.2", " 198.51.100.1 , 203.0.113.2", "2001:db8::1", "\u2003198.51.100.1\u2003,proxy"];
        string?[] cloudflare = [null, "", " ", "198.51.100.2"];
        string?[] authorization = [null, "Basic ignored", "Bearer", "Bearer ", "bEaReR  test-key  "];
        foreach (bool trust in new[] { false, true })
        foreach (var remote in new[] { null, IPAddress.Parse("192.0.2.1"), IPAddress.IPv6Loopback })
        foreach (var cf in cloudflare)
        foreach (var xff in forwarded)
        foreach (var auth in authorization)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remote;
            context.Request.Headers["CF-Connecting-IP"] = cf;
            context.Request.Headers["X-Forwarded-For"] = xff;
            context.Request.Headers.Authorization = auth;
            context.Request.Headers["X-Session-Id"] = "not-an-identity";
            context.Request.Headers["x-api-key"] = "also-not-a-partition";
            string? expectedIp = LegacyClientIp(context, trust);

            Assert.Equal(expectedIp, RequestIdentity.ResolveClientIp(context, trust));
            Assert.Equal(expectedIp ?? "unknown", LoginRateLimiter.ResolveClientIp(context, trust));
            Assert.Equal(LegacyPartition(context, trust), RequestIdentity.ResolvePartitionKey(context, trust));
        }
    }

    [Fact]
    public void MultipleHeaderValues_PreserveJoinedValueSemantics()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = new StringValues([" 192.0.2.1 ", "198.51.100.1"]);
        Assert.Equal("192.0.2.1", RequestIdentity.ResolveClientIp(context, true));
        context.Request.Headers["CF-Connecting-IP"] = new StringValues(["192.0.2.2", "192.0.2.3"]);
        Assert.Equal(LegacyClientIp(context, true), RequestIdentity.ResolveClientIp(context, true));
        Assert.Equal(LegacyPartition(context, true), RequestIdentity.ResolvePartitionKey(context, true));
    }

    [Fact]
    public void LongForwardedChain_AllocatesLessThanLegacySplit()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = " 192.0.2.1 ," + string.Join(',', Enumerable.Repeat("198.51.100.1", 128));
        for (int i = 0; i < 100; i++)
        {
            _ = LegacyClientIp(context, true);
            _ = RequestIdentity.ResolveClientIp(context, true);
        }

        const int iterations = 1000;
        long legacy = Measure(() => LegacyClientIp(context, true), iterations);
        long current = Measure(() => RequestIdentity.ResolveClientIp(context, true), iterations);
        output.WriteLine($"XFF allocation: legacy={legacy / iterations} B/op; current={current / iterations} B/op");
        Assert.Equal(LegacyClientIp(context, true), RequestIdentity.ResolveClientIp(context, true));
        Assert.True(current < legacy / 4, $"Expected no full-chain split: legacy={legacy}, current={current}");
    }

    private static long Measure(Func<string?> action, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            _ = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Frozen ae69560 behavior, including empty/malformed forwarded values and Bearer fallback.
    private static string? LegacyClientIp(HttpContext context, bool trust)
    {
        var headers = context.Request.Headers;
        if (trust && headers.TryGetValue("CF-Connecting-IP", out var cf) && !string.IsNullOrEmpty(cf))
            return cf.ToString();
        if (trust && headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
            return xff.ToString().Split(',')[0].Trim();
        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static string LegacyPartition(HttpContext context, bool trust)
    {
        string? ip = LegacyClientIp(context, trust);
        if (!string.IsNullOrEmpty(ip)) return $"ip:{ip}";
        if (context.Request.Headers.TryGetValue("Authorization", out var auth)
            && auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(auth.ToString().Substring("Bearer ".Length).Trim()));
            return $"auth:{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
        }
        return "anonymous";
    }
}
