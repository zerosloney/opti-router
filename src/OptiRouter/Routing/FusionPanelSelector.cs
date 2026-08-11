using OptiRouter.Configuration;

namespace OptiRouter.Routing;

public sealed record FusionPanelSelection(
    int RequestedSize,
    IReadOnlyList<ModelEndpointOptions> RankedCandidates);

/// <summary>Pure deterministic Fusion panel sizing and soft-diversity ranking.</summary>
public sealed class FusionPanelSelector
{
    public FusionPanelSelection Select(
        RouterDecision decision,
        RoutingOptions options,
        UpstreamQuotaStateStore? quotaStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(options);

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        // 1. Quota-aware filtering: Filter out candidates that are known to be exhausted
        IReadOnlyList<ModelEndpointOptions> viableCandidates = decision.Candidates;
        if (quotaStore is not null)
        {
            var unexhausted = new List<ModelEndpointOptions>();
            foreach (var candidate in decision.Candidates)
            {
                var snapshot = quotaStore.GetSnapshot(candidate.Name);
                if (snapshot?.IsExhausted(now) == true)
                    continue;

                bool lacksTokens = decision.EstimatedInputTokens > 0
                    && snapshot?.TokensRemaining is { } tokens
                    && (snapshot.TokensResetAt is null || snapshot.TokensResetAt > now)
                    && tokens < decision.EstimatedInputTokens;

                if (!lacksTokens)
                    unexhausted.Add(candidate);
            }

            // Fallback to decision candidates if filtering would leave < 2 models
            if (unexhausted.Count >= 2)
                viableCandidates = unexhausted;
        }

        int maximum = Math.Min(options.FusionRouterPanelSize, viableCandidates.Count);
        int requested = maximum;
        if (options.EnableDynamicFusionPanelSize)
        {
            int minimum = Math.Min(options.FusionRouterMinPanelSize, maximum);
            requested = decision.RequestComplexity switch
            {
                RequestComplexity.Simple => minimum,
                RequestComplexity.Standard => Math.Min(maximum, minimum + 1),
                RequestComplexity.Complex => maximum,
                _ => maximum
            };
        }

        if (!options.EnableFusionDiversity || viableCandidates.Count <= 1)
            return new FusionPanelSelection(requested, viableCandidates.ToList());

        var remaining = viableCandidates.Skip(1).ToList();
        var ranked = new List<ModelEndpointOptions> { viableCandidates[0] };
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKnown(ranked[0], providers, families);
        bool useProviderDiversity = providers.Count > 0;
        bool useFamilyDiversity = families.Count > 0;

        while (remaining.Count > 0)
        {
            int bestIndex = 0;
            int bestScore = DiversityScore(
                remaining[0], providers, families, useProviderDiversity, useFamilyDiversity);
            for (int i = 1; i < remaining.Count; i++)
            {
                int score = DiversityScore(
                    remaining[i], providers, families, useProviderDiversity, useFamilyDiversity);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            var selected = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);
            ranked.Add(selected);
            AddKnown(selected, providers, families);
        }

        return new FusionPanelSelection(requested, ranked);
    }

    private static int DiversityScore(
        ModelEndpointOptions model,
        IReadOnlySet<string> providers,
        IReadOnlySet<string> families,
        bool useProviderDiversity,
        bool useFamilyDiversity)
    {
        int score = 0;
        // A value can only be "different" when the already-selected panel has a
        // known value for that dimension. Missing primary metadata must not make a
        // lower-ranked candidate win a diversity bonus by itself.
        if (useProviderDiversity
            && !string.IsNullOrWhiteSpace(model.Provider)
            && !providers.Contains(model.Provider)) score++;
        if (useFamilyDiversity
            && !string.IsNullOrWhiteSpace(model.Family)
            && !families.Contains(model.Family)) score++;
        return score;
    }

    private static void AddKnown(
        ModelEndpointOptions model,
        ISet<string> providers,
        ISet<string> families)
    {
        if (!string.IsNullOrWhiteSpace(model.Provider)) providers.Add(model.Provider);
        if (!string.IsNullOrWhiteSpace(model.Family)) families.Add(model.Family);
    }
}
