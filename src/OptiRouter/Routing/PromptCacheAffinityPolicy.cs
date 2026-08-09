namespace OptiRouter.Routing;

/// <summary>Softly promotes a cached stable-prefix model within the already-filtered candidate set.</summary>
public sealed class PromptCacheAffinityPolicy : IRouterPolicy
{
    private readonly PromptCacheAffinityStore _store;

    public PromptCacheAffinityPolicy(PromptCacheAffinityStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnablePromptCacheAffinity
            || previous.Candidates.Count <= 1
            || (context.Options.Routing.EnableSessionAffinity
                && !string.IsNullOrEmpty(context.SessionId)))
            return previous;

        string? fingerprint = StablePromptFingerprint.Compute(context.Request);
        if (fingerprint is null || !_store.TryGetModel(fingerprint, out string? modelName))
            return previous;

        int index = previous.Candidates.ToList().FindIndex(m =>
            m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        if (index <= 0) return previous;

        var reordered = previous.Candidates.ToList();
        var preferred = reordered[index];
        reordered.RemoveAt(index);
        reordered.Insert(0, preferred);
        return previous with
        {
            Candidates = reordered,
            Reason = $"{previous.Reason}; prompt-cache-affinity: hit"
        };
    }
}
