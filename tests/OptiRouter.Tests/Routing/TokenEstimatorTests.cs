using OptiRouter.Clients;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class TokenEstimatorTests
{
    [Fact]
    public void Estimate_NullOrEmptyMessages_ReturnsZero()
    {
        var request = TestHelpers.BuildRequest();
        Assert.Equal(0, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_AllEmptyContent_ReturnsZero()
    {
        // 空内容条目整体跳过，不计 role 开销。
        var request = TestHelpers.BuildRequest(("user", ""), ("assistant", ""));
        Assert.Equal(0, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_SingleShortMessage_ReturnsPositiveTokens()
    {
        // "1234567890" (10 ASCII) / 4.0 = 2.5 → ceil = 3; + 3 (role) = 6
        var request = TestHelpers.BuildRequest(("user", "1234567890"));
        Assert.Equal(6, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_LongAsciiMessage_ReturnsApproximateTokens()
    {
        // 1000 ASCII / 4.0 = 250; + 3 (role) = 253
        var longContent = new string('a', 1000);
        var request = TestHelpers.BuildRequest(("user", longContent));
        Assert.Equal(253, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_CjkContent_HigherTokenDensityThanAscii()
    {
        // 1000 中文字符 / 1.5 = 666.67 → ceil = 667; + 3 (role) = 670
        // 对比同等长度 ASCII（253），中文 token 数应显著更高。
        var cjkContent = new string('中', 1000);
        var request = TestHelpers.BuildRequest(("user", cjkContent));
        Assert.Equal(670, TokenEstimator.Estimate(request));

        // 混合：500 中文 + 500 ASCII = 500/1.5 + 500/4.0 = 333.33 + 125 = 458.33 → ceil 459; + 3 = 462
        var mixed = new string('中', 500) + new string('a', 500);
        var mixedRequest = TestHelpers.BuildRequest(("user", mixed));
        Assert.Equal(462, TokenEstimator.Estimate(mixedRequest));
    }

    [Fact]
    public void Estimate_MultipleMessages_Accumulates()
    {
        // 汇总字符后一次折算：10 ASCII / 4.0 = 2.5 → ceil 3; + 2 条消息 * 3 (role) = 9
        var request = TestHelpers.BuildRequest(
            ("user", "hello"),
            ("assistant", "world"));
        Assert.Equal(9, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_EmptyContentMessage_SkipsTokenCount()
    {
        // 只有 "hello" 条目计入（空内容条目整体跳过）: 5/4 = 1.25 → ceil 2; + 3 = 5
        var request = TestHelpers.BuildRequest(
            ("user", "hello"),
            ("assistant", ""));
        Assert.Equal(5, TokenEstimator.Estimate(request));
    }
}
