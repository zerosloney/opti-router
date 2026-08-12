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

        var reloaded = CreateService(fixture.Path, clock);
        Assert.Equal(1m, Assert.Single(reloaded.GetAllKeys()).DailySpendUsd);

        clock.Advance(TimeSpan.FromDays(1));
        var afterRollover = reloaded.AuthorizeRequest(plaintext);
        Assert.Equal(ClientKeyAuthorizationStatus.Authorized, afterRollover.Status);
        Assert.Equal(0m, Assert.Single(reloaded.GetAllKeys()).DailySpendUsd);

        reloaded.RecordSpend(info.KeyId, 0.25m);
        var final = CreateService(fixture.Path, clock);
        Assert.Equal(0.25m, Assert.Single(final.GetAllKeys()).DailySpendUsd);
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

    private static ClientKeyService CreateService(string path, TimeProvider? clock = null)
        => new(path, NullLogger<ClientKeyService>.Instance, clock);

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
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
