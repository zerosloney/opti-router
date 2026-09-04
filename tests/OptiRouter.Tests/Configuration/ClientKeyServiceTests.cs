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
