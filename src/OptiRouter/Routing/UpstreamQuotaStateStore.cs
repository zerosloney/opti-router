using System.Collections.Concurrent;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>Immutable process-local quota snapshot for one model.</summary>
public sealed record UpstreamQuotaSnapshot(
    string ModelName,
    long? RequestsRemaining,
    long? TokensRemaining,
    DateTimeOffset? RequestsResetAt,
    DateTimeOffset? TokensResetAt,
    DateTimeOffset? ExhaustedUntil,
    int? LastStatusCode,
    DateTimeOffset ObservedAt)
{
    public bool IsExhausted(DateTimeOffset now)
        => ExhaustedUntil is { } until && until > now;
}

/// <summary>
/// Thread-safe process-local upstream quota memory. Updates may come from serial,
/// Race, Fusion, Cascade, or probe calls; reads are memory-only.
/// </summary>
public sealed class UpstreamQuotaStateStore
{
    private readonly ConcurrentDictionary<string, UpstreamQuotaSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public UpstreamQuotaStateStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public UpstreamQuotaSnapshot? GetSnapshot(string modelName)
        => _snapshots.TryGetValue(modelName, out var snapshot) ? snapshot : null;

    public void Record(string modelName, UpstreamResponseMetadata? metadata, bool rateLimited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (metadata is null && !rateLimited) return;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _snapshots.AddOrUpdate(modelName,
            _ => Create(modelName, null, metadata, rateLimited, now),
            (_, previous) => Create(modelName, previous, metadata, rateLimited, now));
    }

    public int Retain(IEnumerable<string> modelNames)
    {
        var retain = new HashSet<string>(modelNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (string name in _snapshots.Keys)
        {
            if (!retain.Contains(name) && _snapshots.TryRemove(name, out _)) removed++;
        }
        return removed;
    }

    private static UpstreamQuotaSnapshot Create(
        string modelName,
        UpstreamQuotaSnapshot? previous,
        UpstreamResponseMetadata? metadata,
        bool rateLimited,
        DateTimeOffset now)
    {
        long? requestsRemaining = rateLimited
            ? metadata?.RequestsRemaining ?? previous?.RequestsRemaining
            : metadata?.RequestsRemaining;
        long? tokensRemaining = rateLimited
            ? metadata?.TokensRemaining ?? previous?.TokensRemaining
            : metadata?.TokensRemaining;
        DateTimeOffset? currentRequestReset = ResolveReset(
            metadata?.RequestsResetAt, metadata?.RequestsResetAfter, now);
        DateTimeOffset? currentTokenReset = ResolveReset(
            metadata?.TokensResetAt, metadata?.TokensResetAfter, now);
        DateTimeOffset? requestsReset = rateLimited
            ? currentRequestReset ?? previous?.RequestsResetAt
            : currentRequestReset;
        DateTimeOffset? tokensReset = rateLimited
            ? currentTokenReset ?? previous?.TokensResetAt
            : currentTokenReset;

        DateTimeOffset? exhaustedUntil = null;
        if (requestsRemaining == 0 && requestsReset > now)
            exhaustedUntil = requestsReset;
        if (tokensRemaining == 0 && tokensReset > now
            && (exhaustedUntil is null || tokensReset > exhaustedUntil))
            exhaustedUntil = tokensReset;

        if (rateLimited)
        {
            DateTimeOffset? rateLimitReset = metadata?.RetryAfterAt
                ?? (metadata?.RetryAfter is { } retryAfter ? now + retryAfter : null);
            if (rateLimitReset is null || rateLimitReset <= now)
            {
                var knownResets = new[] { requestsReset, tokensReset }
                    .Where(x => x is not null && x > now)
                    .Select(x => x!.Value)
                    .ToList();
                rateLimitReset = knownResets.Count > 0 ? knownResets.Max() : null;
            }
            if (rateLimitReset > now)
                exhaustedUntil = rateLimitReset;
        }

        return new UpstreamQuotaSnapshot(
            modelName,
            requestsRemaining,
            tokensRemaining,
            requestsReset,
            tokensReset,
            exhaustedUntil,
            rateLimited ? 429 : null,
            now);
    }

    private static DateTimeOffset? ResolveReset(
        DateTimeOffset? resetAt,
        TimeSpan? resetAfter,
        DateTimeOffset now)
        => resetAt ?? (resetAfter is { } duration ? now + duration : (DateTimeOffset?)null);
}
