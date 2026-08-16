using OptiRouter.Concurrency;
using Xunit;

namespace OptiRouter.Tests.Concurrency;

public class AdaptiveConcurrencyLimiterTests
{
    [Fact]
    public async Task AcquireAsync_ReturnsDisposableLease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 10);
        using var lease = await limiter.AcquireAsync("test-model");
        Assert.NotNull(lease);
        Assert.Equal(10, limiter.GetCurrentLimit("test-model"));
    }

    [Fact]
    public void RecordRtt_HighRtt_TriggersMultiplicativeDecrease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 20, backoffFactor: 0.8);
        string model = "gpt-4o";

        // Baseline min RTT
        limiter.RecordRtt(model, 100);
        Assert.Equal(20, limiter.GetCurrentLimit(model));

        // High RTT congestion spike (300ms vs 100ms baseline -> gradient = 100/300 = 0.33 < 0.70)
        limiter.RecordRtt(model, 300);

        int currentLimit = limiter.GetCurrentLimit(model);
        Assert.True(currentLimit < 20, $"Current limit should drop below 20, actual: {currentLimit}");
        Assert.Equal(16, currentLimit); // 20 * 0.8 = 16
    }

    [Fact]
    public void RecordRtt_LowRtt_TriggersAdditiveIncrease()
    {
        var limiter = new AdaptiveConcurrencyLimiter(minLimit: 2, maxLimit: 20, backoffFactor: 0.8);
        string model = "deepseek-chat";

        limiter.RecordRtt(model, 100);
        limiter.RecordRtt(model, 300); // Limit drops to 16

        Assert.Equal(16, limiter.GetCurrentLimit(model));

        // Smooth RTT recovery
        limiter.RecordRtt(model, 105); // gradient = 100 / 105 = 0.95 >= 0.85 -> limit increases to 17

        Assert.Equal(17, limiter.GetCurrentLimit(model));
    }
}
