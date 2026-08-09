using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>Memory-only policy that excludes known active exhaustion and softly demotes insufficient headroom.</summary>
public sealed class QuotaAwarePolicy : IRouterPolicy
{
    private readonly UpstreamQuotaStateStore _store;
    private readonly TimeProvider _timeProvider;

    public QuotaAwarePolicy(UpstreamQuotaStateStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableQuotaAwareRouting || previous.Candidates.Count == 0)
            return previous;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var viable = new List<ModelEndpointOptions>(previous.Candidates.Count);
        var insufficient = new List<ModelEndpointOptions>();
        int excluded = 0;

        foreach (var candidate in previous.Candidates)
        {
            var snapshot = _store.GetSnapshot(candidate.Name);
            if (snapshot?.IsExhausted(now) == true)
            {
                excluded++;
                continue;
            }

            bool lacksRequest = snapshot?.RequestsRemaining is { } requests
                && (snapshot.RequestsResetAt is null || snapshot.RequestsResetAt > now)
                && requests < 1;
            bool lacksTokens = context.EstimatedInputTokens > 0
                && snapshot?.TokensRemaining is { } tokens
                && (snapshot.TokensResetAt is null || snapshot.TokensResetAt > now)
                && tokens < context.EstimatedInputTokens;
            (lacksRequest || lacksTokens ? insufficient : viable).Add(candidate);
        }

        var reordered = viable.Concat(insufficient).ToList();
        return previous with
        {
            Candidates = reordered,
            Reason = $"{previous.Reason}; quota-aware: excluded={excluded}, insufficient={insufficient.Count}"
        };
    }
}
