using OptiRouter.Clients;
using OptiRouter.Configuration;
using System.Runtime.CompilerServices;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// StreamingHedgeOrchestrator 首行竞速的单测：胜者判定、备选兜底、双双失败与取消路径。
/// </summary>
public class StreamingHedgeOrchestratorTests
{
    private static ModelEndpointOptions Endpoint(string name)
        => new() { Name = name, BaseUrl = "https://example.com" };

    private static ChatRequest Request()
        => new() { Model = "auto", Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") }, Stream = true };

    private static async IAsyncEnumerable<RawStreamLine> Lines(
        string[] data, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var line in data)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            yield return new RawStreamLine(line, null, null);
        }
    }

    private static async IAsyncEnumerable<RawStreamLine> HangUntilCancelled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield return new RawStreamLine("unreachable", null, null);
    }

    [Fact]
    public async Task PrimaryWinsBeforeDelay_SecondaryNeverStarted()
    {
        int secondaryCalls = 0;
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct) => Lines(new[] { "l1", "l2" }, ct));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) =>
        {
            Interlocked.Increment(ref secondaryCalls);
            return Lines(new[] { "s1" }, ct);
        });
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.FromSeconds(5));

        bool won = await hedge.RaceFirstLineAsync(Request(), CancellationToken.None);

        Assert.True(won);
        Assert.Same(primary, hedge.WinnerClient);
        Assert.Equal("l1", hedge.WinnerFirstLine.Data);
        Assert.Null(hedge.PrimaryFailure);
        Assert.Equal(0, secondaryCalls);

        // 首行已由竞速消费，剩余行经 WinnerEnumerator 继续。
        Assert.True(await hedge.WinnerEnumerator!.MoveNextAsync());
        Assert.Equal("l2", hedge.WinnerEnumerator.Current.Data);
        Assert.False(await hedge.WinnerEnumerator.MoveNextAsync());
        await hedge.DisposeAsync();
    }

    [Fact]
    public async Task SlowPrimary_LosesRace_SecondaryServes()
    {
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct) => HangUntilCancelled(ct));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) => Lines(new[] { "s1", "s2" }, ct));
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.FromMilliseconds(30));

        bool won = await hedge.RaceFirstLineAsync(Request(), CancellationToken.None);

        Assert.True(won);
        Assert.Same(secondary, hedge.WinnerClient);
        Assert.Equal("s1", hedge.WinnerFirstLine.Data);
        Assert.True(await hedge.WinnerEnumerator!.MoveNextAsync());
        Assert.Equal("s2", hedge.WinnerEnumerator.Current.Data);
        Assert.False(await hedge.WinnerEnumerator.MoveNextAsync());
        await hedge.DisposeAsync();
    }

    [Fact]
    public async Task PrimarySyncThrow_FallsBackToSecondary()
    {
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct)
            => throw new HttpRequestException("connect refused"));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) => Lines(new[] { "s1" }, ct));
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.FromSeconds(5));

        bool won = await hedge.RaceFirstLineAsync(Request(), CancellationToken.None);

        Assert.True(won);
        Assert.Same(secondary, hedge.WinnerClient);
        // 主模型同步失败直接走备选，不经 hedge delay——通过 PrimaryFailure 可观测。
        Assert.IsType<HttpRequestException>(hedge.PrimaryFailure);
        await hedge.DisposeAsync();
    }

    [Fact]
    public async Task BothEmptyStreams_ReturnsFalseWithoutFailure()
    {
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct) => Lines(Array.Empty<string>(), ct));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) => Lines(Array.Empty<string>(), ct));
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.Zero);

        bool won = await hedge.RaceFirstLineAsync(Request(), CancellationToken.None);

        Assert.False(won);
        Assert.Null(hedge.PrimaryFailure);
        await hedge.DisposeAsync();
    }

    [Fact]
    public async Task PrimaryFailureWithEmptySecondary_ReturnsFalseExposingPrimaryFailure()
    {
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct)
            => throw new InvalidOperationException("upstream 500"));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) => Lines(Array.Empty<string>(), ct));
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.Zero);

        bool won = await hedge.RaceFirstLineAsync(Request(), CancellationToken.None);

        Assert.False(won);
        Assert.IsType<InvalidOperationException>(hedge.PrimaryFailure);
        await hedge.DisposeAsync();
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceled()
    {
        var primary = new MockModelClient(Endpoint("p"), streamRawFunc: (req, ct) => Lines(new[] { "l1" }, ct));
        var secondary = new MockModelClient(Endpoint("s"), streamRawFunc: (req, ct) => Lines(new[] { "s1" }, ct));
        var hedge = new OptiRouter.Endpoints.StreamingHedgeOrchestrator(primary, secondary, TimeSpan.Zero);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => hedge.RaceFirstLineAsync(Request(), new CancellationToken(canceled: true)));
        await hedge.DisposeAsync();
    }
}
