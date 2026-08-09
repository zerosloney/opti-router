using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Routing;

public sealed class RoutingFoundationTests
{
    [Fact]
    public void CostCalculator_ChargesCacheBreakdownWithConfiguredPrices()
    {
        var model = new ModelEndpointOptions
        {
            InputPricePerMillion = 10m,
            CachedInputPricePerMillion = 1m,
            CacheWriteInputPricePerMillion = 12m,
            OutputPricePerMillion = 20m
        };
        var usage = new ChatUsage
        {
            PromptTokens = 100,
            CompletionTokens = 10,
            CachedInputTokens = 60,
            CacheWriteInputTokens = 10,
            UncachedInputTokens = 30
        };

        decimal cost = CostCalculator.Compute(usage, model);

        Assert.Equal(0.00068m, cost);
    }

    [Fact]
    public void CostCalculator_LegacyUsageAndNullPricesPreserveFullInputPricing()
    {
        var model = new ModelEndpointOptions
        {
            InputPricePerMillion = 10m,
            OutputPricePerMillion = 20m
        };
        var legacy = new ChatUsage { PromptTokens = 100, CompletionTokens = 10 };
        var breakdownWithFallback = legacy with { CachedInputTokens = 60, CacheWriteInputTokens = 10 };

        Assert.Equal(0.0012m, CostCalculator.Compute(legacy, model));
        Assert.Equal(0.0012m, CostCalculator.Compute(breakdownWithFallback, model));
    }

    [Fact]
    public void QuotaAwarePolicy_ExcludesKnownCooldownAndStablyDemotesInsufficientHeadroom()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new UpstreamQuotaStateStore(time);
        store.Record("exhausted", new UpstreamResponseMetadata
        {
            RequestsRemaining = 0,
            RequestsResetAt = time.GetUtcNow().AddMinutes(1)
        }, rateLimited: true);
        store.Record("low", new UpstreamResponseMetadata { TokensRemaining = 5 }, rateLimited: false);

        var options = TestHelpers.BuildOptions(
            ("exhausted", ModelTier.Medium, 1000, 1m),
            ("low", ModelTier.Medium, 1000, 1m),
            ("unknown", ModelTier.Medium, 1000, 1m));
        options.Routing.EnableQuotaAwareRouting = true;
        var (context, decision) = Setup(options, estimatedTokens: 10);

        var result = new QuotaAwarePolicy(store, time).Apply(context, decision);

        Assert.Equal(["unknown", "low"], result.Candidates.Select(x => x.Name));
        time.Advance(TimeSpan.FromMinutes(2));
        var restored = new QuotaAwarePolicy(store, time).Apply(context, decision);
        Assert.Equal(["exhausted", "unknown", "low"], restored.Candidates.Select(x => x.Name));
    }

    [Fact]
    public void PromptAffinity_StoresOnlyHash_Expires_AndNeverReintroducesFilteredModel()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new PromptCacheAffinityStore(time, maxEntries: 10);
        var request = new ChatRequest
        {
            Messages = [ChatMessage.FromText("system", "private stable instruction"), ChatMessage.FromText("user", "hello")],
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" }),
                ["seed"] = JsonSerializer.SerializeToElement(123)
            }
        };
        string fingerprint = StablePromptFingerprint.Compute(request)!;
        store.Record(fingerprint, "preferred", TimeSpan.FromMinutes(1));

        Assert.DoesNotContain("private stable instruction", string.Join(',', store.GetStoredFingerprints()));
        Assert.All(store.GetStoredFingerprints(), key => Assert.Matches("^[0-9A-F]{64}$", key));

        var options = TestHelpers.BuildOptions(
            ("other", ModelTier.Medium, 1000, 1m),
            ("preferred", ModelTier.Medium, 1000, 1m));
        options.Routing.EnablePromptCacheAffinity = true;
        var (context, decision) = Setup(options, request: request);
        var policy = new PromptCacheAffinityPolicy(store);
        Assert.Equal("preferred", policy.Apply(context, decision).Primary.Name);

        var filtered = decision with { Candidates = [options.Models[0]] };
        Assert.Equal("other", policy.Apply(context, filtered).Primary.Name);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("other", policy.Apply(context, decision).Primary.Name);
    }

    [Fact]
    public void StablePromptFingerprint_MalformedNullMessage_DoesNotThrow()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                null!,
                ChatMessage.FromText("system", "stable")
            }
        };

        Assert.NotNull(StablePromptFingerprint.Compute(request));
    }

    [Fact]
    public void PromptAffinity_IsOverriddenByDownstreamQuotaConstraint()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var affinity = new PromptCacheAffinityStore(time);
        var quota = new UpstreamQuotaStateStore(time);
        var request = TestHelpers.BuildRequest(("system", "stable"), ("user", "question"));
        string fingerprint = StablePromptFingerprint.Compute(request)!;
        affinity.Record(fingerprint, "preferred", TimeSpan.FromMinutes(5));
        quota.Record("preferred", new UpstreamResponseMetadata
        {
            RequestsRemaining = 0,
            RequestsResetAt = time.GetUtcNow().AddMinutes(2)
        }, true);
        var options = TestHelpers.BuildOptions(
            ("other", ModelTier.Medium, 1000, 1m),
            ("preferred", ModelTier.Medium, 1000, 1m));
        options.Routing.EnablePromptCacheAffinity = true;
        options.Routing.EnableQuotaAwareRouting = true;
        var (context, decision) = Setup(options, request: request);

        var affinityResult = new PromptCacheAffinityPolicy(affinity).Apply(context, decision);
        var quotaResult = new QuotaAwarePolicy(quota, time).Apply(context, affinityResult);

        Assert.Equal("other", quotaResult.Primary.Name);
        Assert.DoesNotContain(quotaResult.Candidates, x => x.Name == "preferred");
    }

    [Fact]
    public void PromptAffinity_WithSessionId_RemainsActiveWhenSessionAffinityIsDisabled()
    {
        var store = new PromptCacheAffinityStore();
        var request = TestHelpers.BuildRequest(("system", "stable"), ("user", "question"));
        string fingerprint = StablePromptFingerprint.Compute(request)!;
        store.Record(fingerprint, "preferred", TimeSpan.FromMinutes(5));
        var options = TestHelpers.BuildOptions(
            ("other", ModelTier.Medium, 1000, 1m),
            ("preferred", ModelTier.Medium, 1000, 1m));
        options.Routing.EnablePromptCacheAffinity = true;
        options.Routing.EnableSessionAffinity = false;
        var (context, decision) = Setup(options, request: request);
        context = context with { SessionId = "explicit-session" };

        Assert.Equal("preferred", new PromptCacheAffinityPolicy(store).Apply(context, decision).Primary.Name);
    }

    [Fact]
    public void PromptAffinityStore_RejectsNonSha256Keys()
    {
        var store = new PromptCacheAffinityStore();

        Assert.Throws<ArgumentException>(() => store.Record(
            "raw prompt content", "model", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void FusionPanelSelector_PreservesFixedCompatibilityWhenSwitchesOff()
    {
        var options = BuildFusionOptions();
        var decision = Decision(options.Models.ToList(), RequestComplexity.Complex);
        var selection = new FusionPanelSelector().Select(decision, options.Routing);

        Assert.Equal(3, selection.RequestedSize);
        Assert.Equal(options.Models.Select(x => x.Name), selection.RankedCandidates.Select(x => x.Name));
    }

    [Theory]
    [InlineData(RequestComplexity.Simple, 2)]
    [InlineData(RequestComplexity.Standard, 3)]
    [InlineData(RequestComplexity.Complex, 4)]
    [InlineData(RequestComplexity.Unknown, 4)]
    public void FusionPanelSelector_DynamicSizeUsesTypedComplexity(RequestComplexity complexity, int expected)
    {
        var options = BuildFusionOptions();
        options.Routing.EnableDynamicFusionPanelSize = true;
        options.Routing.FusionRouterPanelSize = 4;
        options.Routing.FusionRouterMinPanelSize = 2;

        Assert.Equal(expected, new FusionPanelSelector().Select(
            Decision(options.Models.ToList(), complexity), options.Routing).RequestedSize);
    }

    [Fact]
    public void FusionPanelSelector_SoftlyPrefersProviderAndFamilyDiversity()
    {
        var options = BuildFusionOptions();
        options.Routing.EnableFusionDiversity = true;
        options.Models[0].Provider = "a"; options.Models[0].Family = "x";
        options.Models[1].Provider = "a"; options.Models[1].Family = "x";
        options.Models[2].Provider = "b"; options.Models[2].Family = "y";
        options.Models[3].Provider = "c"; options.Models[3].Family = "x";

        var selection = new FusionPanelSelector().Select(
            Decision(options.Models.ToList(), RequestComplexity.Standard), options.Routing);

        Assert.Equal("m1", selection.RankedCandidates[0].Name);
        Assert.Equal("m3", selection.RankedCandidates[1].Name);
        Assert.Equal("m4", selection.RankedCandidates[2].Name);
    }

    [Fact]
    public void FusionPanelSelector_MissingPrimaryMetadataPreservesCandidateRank()
    {
        var options = BuildFusionOptions();
        options.Routing.EnableFusionDiversity = true;
        options.Models[1].Provider = "provider-b";
        options.Models[1].Family = "family-b";
        options.Models[2].Provider = "provider-b";
        options.Models[2].Family = "family-b";
        options.Models[3].Provider = "provider-c";
        options.Models[3].Family = "family-c";

        var selection = new FusionPanelSelector().Select(
            Decision(options.Models.ToList(), RequestComplexity.Standard), options.Routing);

        Assert.Equal(options.Models.Select(x => x.Name), selection.RankedCandidates.Select(x => x.Name));
    }

    private static RouterOptions BuildFusionOptions()
    {
        var options = TestHelpers.BuildOptions(
            ("m1", ModelTier.Medium, 1000, 1m),
            ("m2", ModelTier.Medium, 1000, 1m),
            ("m3", ModelTier.Medium, 1000, 1m),
            ("m4", ModelTier.Medium, 1000, 1m));
        options.Routing.FusionRouterPanelSize = 3;
        return options;
    }

    private static RouterDecision Decision(IReadOnlyList<ModelEndpointOptions> candidates, RequestComplexity complexity)
        => new() { Candidates = candidates, Reason = "typed", RequestComplexity = complexity };

    private static (RouterContext Context, RouterDecision Decision) Setup(
        RouterOptions options,
        int estimatedTokens = 1,
        ChatRequest? request = null)
    {
        request ??= TestHelpers.BuildRequest(("user", "hello"));
        var context = new RouterContext
        {
            Request = request,
            AllModels = options.Models.ToList(),
            Options = options,
            EstimatedInputTokens = estimatedTokens
        };
        return (context, new RouterDecision
        {
            Candidates = options.Models.ToList(),
            Reason = "initial",
            EstimatedInputTokens = estimatedTokens
        });
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
