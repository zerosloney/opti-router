using System.Net;
using OptiRouter.Clients;
using OptiRouter.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// SafeMessage 错误摘要：上游响应体必须进审计（2026-08 Kimi 400 排查时只有裸状态码，
/// 拒绝原因只能靠猜）。body 单行化 + 300 字符截断防 HTML 大页撑爆 audit 列。
/// </summary>
public class UpstreamFailureClassifierTests
{
    [Fact]
    public void ModelClientException_WithJsonBody_IncludesFirstLine()
    {
        var ex = new ModelClientException(HttpStatusCode.BadRequest,
            responseBody: """{"error":{"message":"context length exceeded"}}""");

        var msg = UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: false);

        Assert.Equal("""upstream-status-400: {"error":{"message":"context length exceeded"}}""", msg);
    }

    [Fact]
    public void ModelClientException_WithoutBody_BareStatus()
    {
        var ex = new ModelClientException(HttpStatusCode.BadGateway, responseBody: null);

        Assert.Equal("upstream-status-502", UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: false));
    }

    [Fact]
    public void QuotaLimited_With429Body_ShowsRetryHint()
    {
        // 上游 429 的 body 通常带 retry-after / reset 提示——区分"上游限流"与本地窗口的唯一现场。
        var ex = new ModelClientException(HttpStatusCode.TooManyRequests,
            responseBody: """{"error":{"message":"rate limit reached","reset":"2s"}}""");

        var msg = UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: true);

        Assert.StartsWith("quota-exhausted:", msg);
        Assert.Contains("rate limit reached", msg);
    }

    [Fact]
    public void QuotaLimited_WithoutBody_KeepsLegacyBareForm()
    {
        var ex = new ModelClientException(HttpStatusCode.TooManyRequests, responseBody: null);

        Assert.Equal("quota-exhausted", UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: true));
    }

    [Fact]
    public void MultiLineBody_CutAtFirstNewline()
    {
        var ex = new ModelClientException(HttpStatusCode.ServiceUnavailable,
            responseBody: "<html>\n<head><title>503</title></head>\n<body>...</body></html>");

        var msg = UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: false);

        Assert.Equal("upstream-status-503: <html>", msg);
    }

    [Fact]
    public void OversizedBody_TruncatedAt300()
    {
        var longLine = new string('x', 1000);
        var ex = new ModelClientException(HttpStatusCode.BadRequest, responseBody: longLine);

        var msg = UpstreamFailureClassifier.SafeMessage(ex, quotaLimited: false);

        Assert.Equal(300 + "upstream-status-400: ".Length + "…".Length, msg.Length);
        Assert.EndsWith("…", msg);
    }

    [Fact]
    public void NonHttpExceptions_KeepLegacyForms()
    {
        Assert.Equal("timeout", UpstreamFailureClassifier.SafeMessage(new OperationCanceledException(), quotaLimited: false));
        Assert.Equal("network-error", UpstreamFailureClassifier.SafeMessage(new HttpRequestException(), quotaLimited: false));
        Assert.Equal("quota-exhausted", UpstreamFailureClassifier.SafeMessage(new InvalidOperationException(), quotaLimited: true));
        Assert.Equal("upstream-error", UpstreamFailureClassifier.SafeMessage(new InvalidOperationException(), quotaLimited: false));
    }
}
