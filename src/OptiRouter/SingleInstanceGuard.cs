namespace OptiRouter;

/// <summary>
/// 单实例守卫：named mutex 判定是否已有【其他进程】在运行，是则干净退出而非端口竞争崩溃
/// （此前重复启动以 Kestrel AddressInUse 异常堆栈告终，与外部守护进程竞态时新旧实例错位）。
/// <para>
/// 同进程多 host（集成测试 WebApplicationFactory）必须放行：
/// 静态标志区分"外部进程持有锁"（退出）与"本进程已持有锁"（放行）。
/// </para>
/// </summary>
public static class SingleInstanceGuard
{
    private static int _heldByThisProcess;

    /// <summary>
    /// 尝试成为唯一实例。返回 true = 可以继续启动；false = 其他进程已在运行（调用方应退出）。
    /// mutex 句柄由返回的 IDisposable 持有至进程结束，进程终止自动释放。
    /// </summary>
    /// <param name="log">失败原因记录回调（可选）。</param>
    /// <returns>锁句柄（保持到进程结束）与是否放行；不放行时句柄为 null。</returns>
    public static (bool Proceed, IDisposable? Lock) TryAcquire(Action<string>? log = null)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\OptiRouter.SingleInstance", out bool createdNew);
        bool acquired = createdNew;
        if (!createdNew)
        {
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; } // 上个持有进程已死：接管
        }

        if (acquired)
        {
            // createdNew 时显式取所有权：否则锁无主，第二个进程的 WaitOne 也能成功，守卫失效。
            try { mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { }
            Volatile.Write(ref _heldByThisProcess, 1);
            return (true, mutex);
        }

        if (Volatile.Read(ref _heldByThisProcess) != 0)
            return (true, null); // 本进程首个 host 已持有：后续 host（测试）放行

        log?.Invoke("Another OptiRouter instance is already running (mutex 'Local\\OptiRouter.SingleInstance'). Exiting.");
        mutex.Dispose();
        return (false, null);
    }
}
