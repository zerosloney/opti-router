using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Configuration;

public sealed class ClientKeyServiceTests
{
    [Fact]
    public void NewFileStartsEmpty_AndCreatedPlaintextIsNeverPersisted()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);

        Assert.Equal("[]", File.ReadAllText(fixture.Path).Trim());

        var (plaintext, info) = service.CreateKey("tenant-a");
        string persisted = File.ReadAllText(fixture.Path);

        Assert.DoesNotContain(plaintext, persisted, StringComparison.Ordinal);
        Assert.Contains(info.KeyHash, persisted, StringComparison.Ordinal);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void AuthorizeRequest_DistinguishesInvalidAndDisabledKeys()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintext, info) = service.CreateKey("tenant-a");

        Assert.Equal(ClientKeyAuthorizationStatus.Invalid, service.AuthorizeRequest("wrong-key").Status);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);

        Assert.True(service.UpdateKey(info.KeyId, enabled: false, dailyBudgetUsd: null, maxQps: null));
        var disabled = service.AuthorizeRequest(plaintext);
        Assert.Equal(ClientKeyAuthorizationStatus.Disabled, disabled.Status);
        Assert.Equal(info.KeyId, disabled.KeyId);
        Assert.Equal("tenant-a", disabled.TenantName);
    }

    [Fact]
    public void AuthorizeRequest_EnforcesFixedQpsWindow_AndRollsOver()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, 900, TimeSpan.Zero));
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path, clock);
        var (plaintext, _) = service.CreateKey("tenant-a", dailyBudgetUsd: 0m, maxQps: 2);

        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
        var limited = service.AuthorizeRequest(plaintext);
        Assert.Equal(ClientKeyAuthorizationStatus.RateLimited, limited.Status);
        Assert.Equal(1, limited.RetryAfterSeconds);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void RecordSpend_PersistsBudget_AndUtcRolloverResetsSpendAfterReload()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 23, 59, 0, TimeSpan.Zero));
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path, clock);
        var (plaintext, info) = service.CreateKey("tenant-a", dailyBudgetUsd: 1m, maxQps: 20);

        service.RecordSpend(info.KeyId, 1m);
        Assert.Equal(ClientKeyAuthorizationStatus.BudgetExhausted, service.AuthorizeRequest(plaintext).Status);

        // RecordSpend 现为去抖落盘：内存值即时生效（上面 BudgetExhausted 已证明），
        // 但跨实例读文件需显式 Flush 才能持久化。
        service.Flush();

        var reloaded = CreateService(fixture.Path, clock);
        Assert.Equal(1m, Assert.Single(reloaded.GetAllKeys()).DailySpendUsd);

        clock.Advance(TimeSpan.FromDays(1));
        var afterRollover = reloaded.AuthorizeRequest(plaintext);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, afterRollover.Status);
        Assert.Equal(0m, Assert.Single(reloaded.GetAllKeys()).DailySpendUsd);

        reloaded.RecordSpend(info.KeyId, 0.25m);
        reloaded.Flush();
        var final = CreateService(fixture.Path, clock);
        Assert.Equal(0.25m, Assert.Single(final.GetAllKeys()).DailySpendUsd);
    }

    [Fact]
    public void RecordSpend_LiveValueIsImmediate_ButFilePersistsOnlyAfterFlush()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var fixture = new TempFixture();
        // 用零宽 interval 关掉后台定时器，避免与断言竞争。
        using var service = CreateService(fixture.Path, clock, flushInterval: TimeSpan.Zero);
        var (_, info) = service.CreateKey("tenant-a", dailyBudgetUsd: 100m, maxQps: 50);

        string beforeBatch = File.ReadAllText(fixture.Path);

        // 连续多笔花费：内存值即时累加，文件不应被改写（去抖）。
        service.RecordSpend(info.KeyId, 1m);
        service.RecordSpend(info.KeyId, 2m);
        service.RecordSpend(info.KeyId, 3m);
        Assert.Equal(6m, Assert.Single(service.GetAllKeys()).DailySpendUsd);
        Assert.Equal(beforeBatch, File.ReadAllText(fixture.Path));

        // Flush 后整批一次性落盘，值正确。
        service.Flush();
        var reloaded = CreateService(fixture.Path, clock);
        Assert.Equal(6m, Assert.Single(reloaded.GetAllKeys()).DailySpendUsd);
    }

    [Fact]
    public void ReserveSpend_BlocksAuthorize_InflightRequestsCannotCollectivelyOverspend()
    {
        // 租户预算 TOCTOU 防护：已入账 0.6 < 预算 1，但另一并发请求 in-flight 预留 0.5——
        // 授权必须读"已入账 + 预留"（1.1 ≥ 1）拒绝，而不是等流结束后计费才反应。
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintext, info) = service.CreateKey("tenant-a", dailyBudgetUsd: 1m, maxQps: 20);

        service.RecordSpend(info.KeyId, 0.6m);
        service.ReserveSpend(info.KeyId, 0.5m);
        Assert.Equal(ClientKeyAuthorizationStatus.BudgetExhausted, service.AuthorizeRequest(plaintext).Status);

        // 预留释放后恢复放行。
        service.ReleaseSpend(info.KeyId, 0.5m);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void ReserveRelease_ClampsAtZero_AndIgnoresInvalidInputs()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintext, info) = service.CreateKey("tenant-a", dailyBudgetUsd: 1m, maxQps: 20);

        service.ReserveSpend(info.KeyId, 2m);
        service.ReleaseSpend(info.KeyId, 3m); // 超额释放 clamp 到 0，不得出现负数
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);

        service.ReserveSpend("   ", 1m);   // 无效 keyId 无效果
        service.ReserveSpend(info.KeyId, 0m); // 非正数无效果
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void ReserveSpend_IsPerKey_OtherTenantUnaffected()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (_, a) = service.CreateKey("tenant-a", dailyBudgetUsd: 1m, maxQps: 20);
        var (plaintextB, _) = service.CreateKey("tenant-b", dailyBudgetUsd: 1m, maxQps: 20);

        service.RecordSpend(a.KeyId, 0.6m);
        service.ReserveSpend(a.KeyId, 0.5m);

        // tenant-b 无预留不受影响（按 keyId 隔离）。
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintextB).Status);
    }

    [Fact]
    public void ExistingHashedFileWithoutDate_RemainsCompatible()
    {
        using var fixture = new TempFixture();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var initial = CreateService(fixture.Path, clock);
        var (plaintext, info) = initial.CreateKey("tenant-a", dailyBudgetUsd: 0m, maxQps: 5);

        string json = File.ReadAllText(fixture.Path).Replace(",\n  \"dailySpendDateUtc\": \"2026-01-02T00:00:00Z\"", string.Empty, StringComparison.Ordinal);
        json = json.Replace(info.KeyHash, info.KeyHash.ToLowerInvariant(), StringComparison.Ordinal);
        File.WriteAllText(fixture.Path, json);

        var reloaded = CreateService(fixture.Path, clock);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, reloaded.AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void CorruptOrLegacyPlaintextFile_IsPreservedAndThrows()
    {
        using var fixture = new TempFixture();
        const string legacy = "[{\"key\":\"opti-key-plaintext\",\"tenantName\":\"legacy\"}]";
        File.WriteAllText(fixture.Path, legacy);

        Assert.ThrowsAny<Exception>(() => CreateService(fixture.Path));
        Assert.Equal(legacy, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public async Task ConcurrentAuthorization_DoesNotExceedQps()
    {
        using var fixture = new TempFixture();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(fixture.Path, clock);
        var (plaintext, _) = service.CreateKey("tenant-a", dailyBudgetUsd: 0m, maxQps: 8);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => service.AuthorizeRequest(plaintext))));

        Assert.Equal(8, outcomes.Count(r => r.Status == ClientKeyAuthorizationStatus.Authorized));
        Assert.Equal(92, outcomes.Count(r => r.Status == ClientKeyAuthorizationStatus.RateLimited));
    }

    /// <summary>
    /// 跨平台注入持久化失败，故障点统一收敛在 PersistKeys 的"临时文件写入"阶段（最确定的一步）：
    /// <list type="bullet">
    /// <item>Windows：以 FileShare.None 独占锁目标文件——PersistKeys 的 File.Replace 对被锁目标抛 IOException；</item>
    /// <item>POSIX：把密钥文件所在目录临时置为只读（摘除写权限）——临时文件 CreateNew 抛
    /// UnauthorizedAccessException。不走"替换目标文件"一步：rename(file→目录) 的 .NET Unix 语义
    /// 有歧义（首版 CI 实测 CreateKey 路径抛、Delete/Update 路径不抛，疑似 RENAME_EXCHANGE 使
    /// 替换成功），只读目录在写阶段必然失败，与后续分支无关。</item>
    /// </list>
    /// 断言用 <see cref="AssertPersistThrows"/>（IOException 或 UnauthorizedAccessException 皆计为
    /// 持久化失败），三个用例的故障后断言与恢复重试语义不变。
    /// </summary>
    private static IDisposable InjectPersistFailure(TempFixture fixture)
        => OperatingSystem.IsWindows()
            ? new WindowsExclusiveFileLock(fixture.Path)
            : new PosixReadOnlyDirectory(fixture.Path);

    /// <summary>PersistKeys 同步传播持久化失败：Windows 注入呈 IOException，POSIX 只读目录呈 UnauthorizedAccessException。</summary>
    private static void AssertPersistThrows(Action persistOperation)
    {
        var ex = Record.Exception(persistOperation);
        Assert.True(ex is IOException or UnauthorizedAccessException,
            $"Expected IOException (Windows) or UnauthorizedAccessException (POSIX read-only dir); got: {ex?.GetType().Name ?? "null"}");
    }

    private sealed class WindowsExclusiveFileLock(string path) : IDisposable
    {
        private readonly FileStream _lock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        public void Dispose() => _lock.Dispose();
    }

    private sealed class PosixReadOnlyDirectory : IDisposable
    {
        private readonly string _directory;
        private readonly bool _permissionsApplied;
        private UnixFileMode _original;

        public PosixReadOnlyDirectory(string keyFilePath)
        {
            _directory = Path.GetDirectoryName(keyFilePath)!;
            // 守卫用 CA1416 分析器认可的 OperatingSystem 方法（效果同 ClientKeyService 的 RuntimeInformation 口径）
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _original = File.GetUnixFileMode(_directory);
                File.SetUnixFileMode(_directory,
                    _original & ~UnixFileMode.UserWrite & ~UnixFileMode.GroupWrite & ~UnixFileMode.OtherWrite);
                _permissionsApplied = true;
            }
        }

        public void Dispose()
        {
            if (_permissionsApplied && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                File.SetUnixFileMode(_directory, _original);
        }
    }

    /// <summary>
    /// P1-3 回归：持久化失败时密钥修改不得生效。注入方式见 <see cref="InjectPersistFailure"/>。
    /// 修复前：缓存先改后持久化，异常后内存对象已被改写——禁用失败却鉴权 Disabled、
    /// 删除失败却 Invalid；修复后失败前后鉴权口径与磁盘一致。
    /// </summary>
    [Fact]
    public void UpdateKey_PersistFails_CacheKeepsOldEnabledState()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintext, info) = service.CreateKey("tenant-a");
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);

        using (InjectPersistFailure(fixture))
        {
            AssertPersistThrows(() => service.UpdateKey(info.KeyId, enabled: false, dailyBudgetUsd: null, maxQps: null));
        }

        // 失败后：本进程缓存与磁盘持久状态都必须仍是启用。
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
        var reloaded = CreateService(fixture.Path);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, reloaded.AuthorizeRequest(plaintext).Status);

        // 故障恢复后重试同一修改：正常生效。
        Assert.True(service.UpdateKey(info.KeyId, enabled: false, dailyBudgetUsd: null, maxQps: null));
        Assert.Equal(ClientKeyAuthorizationStatus.Disabled, service.AuthorizeRequest(plaintext).Status);
        Assert.Equal(ClientKeyAuthorizationStatus.Disabled, CreateService(fixture.Path).AuthorizeRequest(plaintext).Status);
    }

    [Fact]
    public void DeleteKey_PersistFails_KeyRemainsAuthorizable()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintext, info) = service.CreateKey("tenant-a");

        using (InjectPersistFailure(fixture))
        {
            AssertPersistThrows(() => service.DeleteKey(info.KeyId));
        }

        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintext).Status);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, CreateService(fixture.Path).AuthorizeRequest(plaintext).Status);
        Assert.Single(service.GetAllKeys());

        // 故障恢复后重试删除：正常生效。
        Assert.True(service.DeleteKey(info.KeyId));
        Assert.Equal(ClientKeyAuthorizationStatus.Invalid, service.AuthorizeRequest(plaintext).Status);
        Assert.Empty(CreateService(fixture.Path).GetAllKeys());
    }

    [Fact]
    public void CreateKey_PersistFails_NewKeyNeverBecomesAuthorizable()
    {
        using var fixture = new TempFixture();
        var service = CreateService(fixture.Path);
        var (plaintextA, _) = service.CreateKey("tenant-a");

        using (InjectPersistFailure(fixture))
        {
            AssertPersistThrows(() => service.CreateKey("tenant-b"));
        }

        Assert.Single(service.GetAllKeys());
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, service.AuthorizeRequest(plaintextA).Status);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, CreateService(fixture.Path).AuthorizeRequest(plaintextA).Status);
    }

    private static ClientKeyService CreateService(string path, TimeProvider? clock = null, TimeSpan? flushInterval = null)
        => new(path, NullLogger<ClientKeyService>.Instance, clock, flushInterval);

    private sealed class TempFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "optirouter-client-key-" + Guid.NewGuid().ToString("N"));

        public TempFixture()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "client-keys.json");
        }

        public string Path { get; }

        public void Dispose()
        {
            // best-effort 清理：落盘与删除目录之间的收尾竞态不抖红测试（同 TenantKeyFixture 约定）。
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
