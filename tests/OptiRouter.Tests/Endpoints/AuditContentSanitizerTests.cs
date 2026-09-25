using OptiRouter.Clients;
using OptiRouter.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 审计内容兜底脱敏：请求摘要落库前的密钥掩码（独立于上游 PII 脱敏开关）。
/// 只打高置信密钥形态，正常问答内容必须原样保留。
/// </summary>
public sealed class AuditContentSanitizerTests
{
    [Fact]
    public void NullAndEmpty_PassThrough()
    {
        Assert.Null(AuditContentSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, AuditContentSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void OpenAiStyleKey_IsMasked()
    {
        var sanitized = AuditContentSanitizer.Sanitize("我的密钥 sk-abcdefgh12345678WXYZ-_ 请处理");
        Assert.Contains("sk-***", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void BearerToken_IsMasked()
    {
        var sanitized = AuditContentSanitizer.Sanitize("Authorization: Bearer abc123def456ghi789 已泄露");
        Assert.Contains("Bearer ***", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123def456", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Jwt_IsMasked()
    {
        string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9P";
        var sanitized = AuditContentSanitizer.Sanitize($"token: {jwt}");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIs", sanitized, StringComparison.Ordinal);
        Assert.Contains("***", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformKeyShapes_AreMasked()
    {
        Assert.DoesNotContain("ghp_", AuditContentSanitizer.Sanitize("ghp_0123456789abcdefghijklmnopqrst"), StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", AuditContentSanitizer.Sanitize("AKIAIOSFODNN7EXAMPLE"), StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-", AuditContentSanitizer.Sanitize("xoxb-123456789012-abc"), StringComparison.Ordinal);
        Assert.DoesNotContain("AIza", AuditContentSanitizer.Sanitize("AIzaSyA0123456789abcdefghijklmnopqrstuvwx"), StringComparison.Ordinal);
        Assert.DoesNotContain("LTAI", AuditContentSanitizer.Sanitize("LTAI5tAbCdEfGhIjKlMnOpQr"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api_key=supersecretvalue123")]
    [InlineData("apikey=supersecretvalue123")]
    [InlineData("access_key: supersecretvalue123")]
    [InlineData("\"api_key\":\"supersecretvalue123\"")]
    [InlineData("\"password\":\"hunter2secret\"")]
    public void KeyValueSecrets_AreMasked(string input)
    {
        var sanitized = AuditContentSanitizer.Sanitize($"配置 {input} 请检查");
        Assert.DoesNotContain("supersecretvalue123", sanitized, StringComparison.Ordinal);
        Assert.Contains("***", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalContent_IsUntouched()
    {
        const string normal = "请帮我写一个冒泡排序，时间复杂度要求 O(n²)，参考 skill 树的学习路径。";
        Assert.Equal(normal, AuditContentSanitizer.Sanitize(normal));
    }

    [Fact]
    public void MaskSurvivesTruncation_Scenario()
    {
        // 与 ExtractRequestContentSummary 相同的管道：先截断 500 再脱敏。
        string longText = new string('x', 490) + " sk-abcdefgh12345678WXYZ-_ tail";
        string truncated = longText[..500] + "...";
        var sanitized = AuditContentSanitizer.Sanitize(truncated);

        Assert.True(sanitized!.Length <= 510);
        Assert.DoesNotContain("sk-abcdefgh", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_NeverThrows()
    {
        // 恶意/畸形输入不抛异常（脱敏是持久化前卫生步骤，失败必须保守降级）。
        Assert.NotNull(AuditContentSanitizer.Sanitize(new string('\ud800', 10)));
    }
}
