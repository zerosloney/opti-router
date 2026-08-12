using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Endpoints;

public sealed class OutcomeRecorderTenantTests
{
    [Fact]
    public void AuthorizedTenantCost_AccumulatesLedgerAndTenantSpend()
    {
        using var fixture = new TenantFixture();
        var ledger = new CostLedger();
        var recorder = CreateRecorder(ledger, fixture.Service, ContextWith(fixture.AuthorizedIdentity));

        recorder.RecordCost(1.25m, sessionId: null);

        Assert.Equal(1.25m, ledger.GetSpend().Total);
        Assert.Equal(1.25m, Assert.Single(fixture.Service.GetAllKeys()).DailySpendUsd);
    }

    [Fact]
    public void GlobalKeyContext_DoesNotAccumulateTenantSpend()
    {
        using var fixture = new TenantFixture();
        var ledger = new CostLedger();
        var recorder = CreateRecorder(
            ledger,
            fixture.Service,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        recorder.RecordCost(0.75m, sessionId: null);

        Assert.Equal(0.75m, ledger.GetSpend().Total);
        Assert.Equal(0m, Assert.Single(fixture.Service.GetAllKeys()).DailySpendUsd);
    }

    [Fact]
    public void NoHttpContext_DoesNotAccumulateTenantSpend()
    {
        using var fixture = new TenantFixture();
        var ledger = new CostLedger();
        var recorder = CreateRecorder(ledger, fixture.Service, new HttpContextAccessor());

        recorder.RecordCost(0.5m, sessionId: null);

        Assert.Equal(0.5m, ledger.GetSpend().Total);
        Assert.Equal(0m, Assert.Single(fixture.Service.GetAllKeys()).DailySpendUsd);
    }

    [Theory]
    [InlineData(ClientKeyAuthorizationStatus.Invalid)]
    [InlineData(ClientKeyAuthorizationStatus.Disabled)]
    [InlineData(ClientKeyAuthorizationStatus.RateLimited)]
    [InlineData(ClientKeyAuthorizationStatus.BudgetExhausted)]
    public void NonAuthorizedTenantIdentity_DoesNotAccumulateSpend(ClientKeyAuthorizationStatus status)
    {
        using var fixture = new TenantFixture();
        var ledger = new CostLedger();
        var identity = new ClientKeyAuthorizationResult(status, fixture.Info.KeyId, "tenant-a");
        var recorder = CreateRecorder(ledger, fixture.Service, ContextWith(identity));

        recorder.RecordCost(0.5m, sessionId: null);

        Assert.Equal(0.5m, ledger.GetSpend().Total);
        Assert.Equal(0m, Assert.Single(fixture.Service.GetAllKeys()).DailySpendUsd);
    }

    [Fact]
    public void TenantPersistenceFailure_DoesNotBlockLedgerRecording()
    {
        using var fixture = new TenantFixture();
        var ledger = new CostLedger();
        var recorder = CreateRecorder(ledger, fixture.Service, ContextWith(fixture.AuthorizedIdentity));
        File.WriteAllText(fixture.FilePath, "not-json");

        recorder.RecordCost(2m, sessionId: null);

        Assert.Equal(2m, ledger.GetSpend().Total);
    }

    private static OutcomeRecorder CreateRecorder(
        CostLedger ledger,
        ClientKeyService? clientKeyService,
        IHttpContextAccessor? httpContextAccessor)
        => new(
            auditStore: null!,
            metrics: null!,
            ledger: ledger,
            options: null!,
            affinityCache: null!,
            tsStore: null!,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance,
            clientKeyService: clientKeyService,
            httpContextAccessor: httpContextAccessor);

    private static IHttpContextAccessor ContextWith(ClientKeyAuthorizationResult identity)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(ClientKeyAuthorizationResult)] = identity;
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class TenantFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "optirouter-outcome-" + Guid.NewGuid().ToString("N"));

        public TenantFixture()
        {
            Directory.CreateDirectory(_directory);
            FilePath = System.IO.Path.Combine(_directory, "client-keys.json");
            Service = new ClientKeyService(FilePath, NullLogger<ClientKeyService>.Instance);
            (_, Info) = Service.CreateKey("tenant-a");
        }

        public string FilePath { get; }

        public ClientKeyService Service { get; }

        public ClientKeyInfo Info { get; }

        public ClientKeyAuthorizationResult AuthorizedIdentity
            => new(ClientKeyAuthorizationStatus.Authorized, Info.KeyId, "tenant-a");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
