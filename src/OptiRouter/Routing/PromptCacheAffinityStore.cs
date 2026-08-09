using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>Bounded, TTL-based cache that stores only SHA-256 fingerprints and model names.</summary>
public sealed class PromptCacheAffinityStore
{
    private const int DefaultMaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly object _evictionLock = new();

    private sealed record Entry(string ModelName, DateTimeOffset ExpiresAt, DateTimeOffset RecordedAt);

    public PromptCacheAffinityStore(TimeProvider? timeProvider = null, int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxEntries = maxEntries;
    }

    public void Record(string fingerprint, string modelName, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (fingerprint.Length != 64 || fingerprint.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Fingerprint must be a SHA-256 hexadecimal digest.", nameof(fingerprint));
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _entries[fingerprint] = new Entry(modelName, now + ttl, now);
        Trim(now);
    }

    public bool TryGetModel(string fingerprint, out string? modelName)
    {
        modelName = null;
        if (!_entries.TryGetValue(fingerprint, out var entry)) return false;
        if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _entries.TryRemove(fingerprint, out _);
            return false;
        }
        modelName = entry.ModelName;
        return true;
    }

    public IReadOnlyCollection<string> GetStoredFingerprints() => _entries.Keys.ToArray();

    private void Trim(DateTimeOffset now)
    {
        if (_entries.Count <= _maxEntries) return;
        lock (_evictionLock)
        {
            foreach (var pair in _entries.Where(p => p.Value.ExpiresAt <= now))
                _entries.TryRemove(pair.Key, out _);
            int excess = _entries.Count - _maxEntries;
            if (excess <= 0) return;
            foreach (string key in _entries.OrderBy(p => p.Value.RecordedAt).Take(excess).Select(p => p.Key))
                _entries.TryRemove(key, out _);
        }
    }
}
