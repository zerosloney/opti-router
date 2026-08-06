using OptiRouter.Routing;
using SharpToken;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class TiktokenTokenEstimatorTests
{
    // 参考值（o200k_base 实测）：
    //   "hello world" -> 2 tokens
    //   "1234567890"  -> 4 tokens
    //   1000 x 'a'    -> 125 tokens
    //   1000 x '中'   -> 1000 tokens
    // 每条非空消息另计 3 token 开销（role 标记 + 分隔符）。

    [Fact]
    public void Estimate_NullOrEmptyMessages_ReturnsZero()
    {
        var estimator = new TiktokenTokenEstimator();
        var request = TestHelpers.BuildRequest();
        Assert.Equal(0, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_AllEmptyContent_ReturnsZero()
    {
        var estimator = new TiktokenTokenEstimator();
        var request = TestHelpers.BuildRequest(("user", ""), ("assistant", ""));
        Assert.Equal(0, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_ShortAscii_MatchesRealBpe()
    {
        var estimator = new TiktokenTokenEstimator();
        // "hello world" = 2 tokens (BPE) + 3 (消息开销) = 5
        var request = TestHelpers.BuildRequest(("user", "hello world"));
        Assert.Equal(5, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_LongAscii_UsesBpeNotBucket()
    {
        var estimator = new TiktokenTokenEstimator();
        // 1000 x 'a'：BPE 压缩重复 -> 125 tokens + 3 = 128。
        // 分桶粗估会给出 253（1000/4 + 3），两者显著不同，证明走了真实 BPE。
        var request = TestHelpers.BuildRequest(("user", new string('a', 1000)));
        int estimated = estimator.Estimate(request);
        Assert.Equal(128, estimated);
        Assert.NotEqual(TokenEstimator.Estimate(request), estimated);
    }

    [Fact]
    public void Estimate_CjkContent_MatchesRealBpe()
    {
        var estimator = new TiktokenTokenEstimator();
        // 1000 个 '中' = 1000 tokens + 3 = 1003
        var request = TestHelpers.BuildRequest(("user", new string('中', 1000)));
        Assert.Equal(1003, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_MultipleMessages_AccumulatesWithPerMessageOverhead()
    {
        var estimator = new TiktokenTokenEstimator();
        // "hello world" (2) + "1234567890" (4) + 2 条消息 * 3 = 12
        var request = TestHelpers.BuildRequest(
            ("user", "hello world"),
            ("assistant", "1234567890"));
        Assert.Equal(12, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_EmptyContentMessage_SkipsTokenCount()
    {
        var estimator = new TiktokenTokenEstimator();
        // 空内容条目整体跳过（不计开销）：2 + 3 = 5
        var request = TestHelpers.BuildRequest(
            ("user", "hello world"),
            ("assistant", ""));
        Assert.Equal(5, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_MatchesDirectGptEncodingCount()
    {
        // 交叉验证：估算 = 逐消息 GptEncoding.CountTokens 之和 + 3*非空消息数
        var estimator = new TiktokenTokenEstimator("cl100k_base");
        var encoding = GptEncoding.GetEncoding("cl100k_base");
        var request = TestHelpers.BuildRequest(
            ("system", "You are a helpful assistant."),
            ("user", "用一句话解释什么是多态。"),
            ("assistant", "多态是同一接口在不同实现下表现出不同行为。"));

        int expected = encoding.CountTokens("You are a helpful assistant.")
            + encoding.CountTokens("用一句话解释什么是多态。")
            + encoding.CountTokens("多态是同一接口在不同实现下表现出不同行为。")
            + 3 * 3;

        Assert.Equal(expected, estimator.Estimate(request));
    }

    [Fact]
    public void Constructor_UnknownEncoding_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TiktokenTokenEstimator("does_not_exist_xyz"));
    }

    [Fact]
    public void Constructor_EmptyEncoding_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TiktokenTokenEstimator("  "));
    }

    [Fact]
    public void IsEncodingAvailable_KnownAndUnknown()
    {
        Assert.True(TiktokenTokenEstimator.IsEncodingAvailable("o200k_base"));
        Assert.True(TiktokenTokenEstimator.IsEncodingAvailable("cl100k_base"));
        Assert.False(TiktokenTokenEstimator.IsEncodingAvailable("does_not_exist_xyz"));
        Assert.False(TiktokenTokenEstimator.IsEncodingAvailable(""));
    }

    [Fact]
    public void Estimate_CounterThrows_FallsBackToBucket()
    {
        // 计数委托抛异常时回退到分桶粗估，路由不被阻塞。
        var estimator = new TiktokenTokenEstimator(_ => throw new InvalidOperationException("boom"));
        var request = TestHelpers.BuildRequest(("user", "1234567890"));

        int expected = TokenEstimator.Estimate(request); // 桶估：10/4 -> ceil 3 + 3 = 6
        Assert.Equal(expected, estimator.Estimate(request));
    }

    [Fact]
    public void Estimate_ConcurrentCalls_ConsistentResults()
    {
        // 并发计数应得到与单线程一致的结果（内部加锁串行化 SharpToken 调用）。
        var estimator = new TiktokenTokenEstimator();
        var request = TestHelpers.BuildRequest(
            ("user", new string('a', 500) + new string('中', 500)),
            ("assistant", "hello world"));
        int expected = estimator.Estimate(request);

        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(expected, estimator.Estimate(request));
        });
    }

    [Fact]
    public void DefaultEncoding_IsO200kBase()
    {
        Assert.Equal("o200k_base", TiktokenTokenEstimator.DefaultEncodingName);
    }
}
