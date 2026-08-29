using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>
/// 对两个流式模型做首行竞速：主模型立即启动，备选模型在延迟后启动，首个产出
/// <see cref="RawStreamLine"/> 的模型获胜。
/// </summary>
/// <remarks>
/// 此类只负责流生命周期和竞速，不负责路由、审计、熔断或配置。每个实例只用于一次竞速；
/// <see cref="RaceFirstLineAsync"/> 返回 true 后调用方从 <see cref="WinnerEnumerator"/> 接管
/// 胜者流（含已取出的 <see cref="WinnerFirstLine"/>），流结束后须调用 <see cref="DisposeAsync"/>
/// 释放胜者内部资源；返回 false 时双方已收尾，失败原因见 <see cref="PrimaryFailure"/>。
/// </remarks>
internal sealed class StreamingHedgeOrchestrator
{
    private readonly IModelClient _primary;
    private readonly IModelClient _secondary;
    private readonly TimeSpan _secondaryDelay;
    private CandidateStream? _primaryStream;
    private CandidateStream? _secondaryStream;

    public StreamingHedgeOrchestrator(IModelClient primary, IModelClient secondary, TimeSpan secondaryDelay)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        if (secondaryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(secondaryDelay), "Hedge delay cannot be negative.");

        _primary = primary;
        _secondary = secondary;
        _secondaryDelay = secondaryDelay;
    }

    /// <summary>
    /// 实际产出首行的模型；仅在 <see cref="RaceFirstLineAsync"/> 返回 true 后可读取。
    /// </summary>
    public IModelClient? WinnerClient { get; private set; }

    /// <summary>
    /// 胜者流的枚举器；首个 MoveNextAsync 已消费，首行内容在 <see cref="WinnerFirstLine"/>。
    /// 剩余行由调用方驱动，枚举器的 Dispose 也归调用方。
    /// </summary>
    public IAsyncEnumerator<RawStreamLine>? WinnerEnumerator { get; private set; }

    /// <summary>
    /// 胜者流的首行（<see cref="WinnerEnumerator"/> 已消费的 MoveNextAsync 对应的 Current）。
    /// </summary>
    public RawStreamLine WinnerFirstLine { get; private set; } = default!;

    /// <summary>
    /// 主模型在首行前的失败；null 表示主模型要么获胜、要么空流正常结束。
    /// </summary>
    public Exception? PrimaryFailure { get; private set; }

    /// <summary>
    /// 执行一次首行竞速。返回 true 表示某一方产出首行（读 <see cref="WinnerClient"/> 等）；
    /// 返回 false 表示双方都未产出首行且未发生外部取消（读 <see cref="PrimaryFailure"/>）。
    /// 外部取消以 <see cref="OperationCanceledException"/> 抛出。
    /// </summary>
    public async Task<bool> RaceFirstLineAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_primaryStream is not null)
            throw new InvalidOperationException("Hedge race already ran; the orchestrator is single-use.");

        var primary = new CandidateStream(_primary, cancellationToken);
        var secondary = new CandidateStream(_secondary, cancellationToken);
        _primaryStream = primary;
        _secondaryStream = secondary;

        try
        {
            primary.Start(request);

            CandidateStream? winner;
            if (primary.Failure is not null)
            {
                // 主模型同步失败时不要再白等 hedge delay，立即启用备选。
                await primary.DisposeAsync(cancel: true).ConfigureAwait(false);
                secondary.Start(request);
                winner = await WaitForFirstLineAsync(cancellationToken, secondary).ConfigureAwait(false);
            }
            else if (_secondaryDelay == TimeSpan.Zero)
            {
                secondary.Start(request);
                winner = await WaitForFirstLineAsync(cancellationToken, primary, secondary).ConfigureAwait(false);
            }
            else
            {
                // 主模型的 MoveNextAsync 已经被调用；delay 只控制备选模型的启动时刻。
                var delayTask = Task.Delay(_secondaryDelay, cancellationToken);
                var primaryMove = primary.PendingMove
                    ?? throw new InvalidOperationException("Primary stream was not started.");
                var firstSignal = await Task.WhenAny(primaryMove, delayTask).ConfigureAwait(false);

                if (firstSignal == primaryMove)
                {
                    winner = await WaitForFirstLineAsync(cancellationToken, primary).ConfigureAwait(false);
                    if (winner is null)
                    {
                        // 空流或首行前失败都不算胜出；此时立即尝试备选。
                        await primary.DisposeAsync(cancel: true).ConfigureAwait(false);
                        secondary.Start(request);
                        winner = await WaitForFirstLineAsync(cancellationToken, secondary).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 外部取消优先于启动备选；正常 delay 到期才进入双路竞速。
                    cancellationToken.ThrowIfCancellationRequested();
                    secondary.Start(request);
                    winner = await WaitForFirstLineAsync(cancellationToken, primary, secondary).ConfigureAwait(false);
                }
            }

            PrimaryFailure = primary.Failure;

            if (winner is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            WinnerClient = winner.Client;
            WinnerEnumerator = winner.Enumerator;
            WinnerFirstLine = winner.Enumerator!.Current;
            var loser = ReferenceEquals(winner, primary) ? secondary : primary;
            await loser.DisposeAsync(cancel: true).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // 竞速未产出胜者（双双失败/外部取消）时在此兜底收尾双方；
            // 产出胜者时胜者流留给调用方，DisposeAsync 统一释放。
            if (WinnerEnumerator is null)
            {
                await primary.DisposeAsync(cancel: true).ConfigureAwait(false);
                await secondary.DisposeAsync(cancel: true).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 释放竞速双方的全部资源（含调用方已接管并 dispose 过枚举器的胜者流内部 CTS）。
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_primaryStream is not null)
            await _primaryStream.DisposeAsync(cancel: true).ConfigureAwait(false);
        if (_secondaryStream is not null)
            await _secondaryStream.DisposeAsync(cancel: true).ConfigureAwait(false);
    }

    private static async Task<CandidateStream?> WaitForFirstLineAsync(
        CancellationToken cancellationToken,
        params CandidateStream[] candidates)
    {
        var pending = candidates
            .Where(candidate => candidate.PendingMove is not null && candidate.Failure is null)
            .ToList();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tasks = pending
                .Select(candidate => candidate.PendingMove!)
                .ToArray();
            var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
            var completed = pending.First(candidate => ReferenceEquals(candidate.PendingMove, completedTask));
            pending.Remove(completed);
            completed.PendingMove = null;

            try
            {
                if (await completedTask.ConfigureAwait(false))
                    return completed;
            }
            catch (Exception ex)
            {
                completed.Failure = ex;
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 空流或首行前异常不再参与竞速；及时释放其连接/枚举器。
            await completed.DisposeAsync(cancel: true).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<RawStreamLine> enumerator)
    {
        return await enumerator.MoveNextAsync().ConfigureAwait(false);
    }

    private sealed class CandidateStream
    {
        public CandidateStream(IModelClient client, CancellationToken cancellationToken)
        {
            Client = client;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public IModelClient Client { get; }

        public CancellationTokenSource Cancellation { get; }

        public IAsyncEnumerator<RawStreamLine>? Enumerator { get; private set; }

        public Task<bool>? PendingMove { get; set; }

        public Exception? Failure { get; set; }

        private bool IsDisposed { get; set; }

        public void Start(ChatRequest request)
        {
            try
            {
                var stream = Client.StreamRawAsync(request, Cancellation.Token);
                Enumerator = stream.GetAsyncEnumerator(Cancellation.Token);
                PendingMove = MoveNextAsync(Enumerator);
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
        }

        public async ValueTask DisposeAsync(bool cancel)
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            if (cancel)
            {
                try
                {
                    Cancellation.Cancel();
                }
                catch
                {
                    // Cancellation callbacks belong to the model client; cleanup must continue.
                }
            }

            if (PendingMove is not null)
            {
                try
                {
                    await PendingMove.ConfigureAwait(false);
                }
                catch
                {
                    // The terminal error is handled by the racing path; cleanup must still dispose the enumerator.
                }
                finally
                {
                    PendingMove = null;
                }
            }

            if (Enumerator is not null)
            {
                try
                {
                    await Enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Do not mask the selected stream's result with a loser/cleanup exception.
                }
                finally
                {
                    Enumerator = null;
                }
            }

            Cancellation.Dispose();
        }
    }
}
