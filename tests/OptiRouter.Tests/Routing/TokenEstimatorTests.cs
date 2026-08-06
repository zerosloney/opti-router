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
    public void Estimate_SingleShortMessage_ReturnsPositiveTokens()
    {
        // "1234567890" (10) + role "user" (4) = 14 chars total
        // ceil(14 / 3.5) = 4
        var request = TestHelpers.BuildRequest(("user", "1234567890"));
        Assert.Equal(4, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_LongMessage_ReturnsApproximateTokens()
    {
        // 1000 'a' + role "user" (4) = 1004 chars total
        // ceil(1004 / 3.5) = ceil(286.857) = 287
        var longContent = new string('a', 1000);
        var request = TestHelpers.BuildRequest(("user", longContent));
        Assert.Equal(287, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_MultipleMessages_Accumulates()
    {
        // "hello" (5) + role "user" (4) = 9 → ceil(9/3.5)=3
        // "world" (5) + role "assistant" (9) = 14 → ceil(14/3.5)=4
        // Total = 7
        var request = TestHelpers.BuildRequest(
            ("user", "hello"),
            ("assistant", "world"));
        Assert.Equal(7, TokenEstimator.Estimate(request));
    }

    [Fact]
    public void Estimate_EmptyContentMessage_SkipsTokenCount()
    {
        var request = TestHelpers.BuildRequest(
            ("user", "hello"),
            ("assistant", ""));
        // "hello" (5) + "user" (4) = 9 → ceil(9/3.5)=3
        Assert.Equal(3, TokenEstimator.Estimate(request));
    }
}
