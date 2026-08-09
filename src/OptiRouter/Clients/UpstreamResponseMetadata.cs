using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace OptiRouter.Clients;

/// <summary>
/// Normalized, immutable upstream response metadata. Only known capacity fields are
/// retained; raw headers are never stored or logged.
/// </summary>
public sealed record UpstreamResponseMetadata
{
    public long? RequestLimit { get; init; }
    public long? RequestsRemaining { get; init; }
    public long? TokenLimit { get; init; }
    public long? TokensRemaining { get; init; }
    public DateTimeOffset? RequestsResetAt { get; init; }
    public DateTimeOffset? TokensResetAt { get; init; }
    public DateTimeOffset? RetryAfterAt { get; init; }
    public TimeSpan? RequestsResetAfter { get; init; }
    public TimeSpan? TokensResetAfter { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Elapsed time until upstream response headers were available. For non-streaming
    /// calls this is the available TTFT proxy, not literal first-token latency.
    /// </summary>
    public long? ResponseHeaderLatencyMs { get; init; }

    /// <summary>Elapsed time until the first upstream SSE data item.</summary>
    public long? TimeToFirstTokenMs { get; init; }
}

/// <summary>Normalizes supported rate-limit headers without retaining raw values.</summary>
public static class UpstreamResponseMetadataNormalizer
{
    private static readonly Regex DurationPartRegex = new(
        @"(?<value>\d+(?:\.\d+)?)(?<unit>ms|s|m|h|d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static UpstreamResponseMetadata Normalize(
        HttpResponseMessage response,
        long responseHeaderLatencyMs,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        DateTimeOffset now = observedAt ?? DateTimeOffset.UtcNow;

        long? requestLimit = ParseNonNegativeInteger(GetFirst(response.Headers,
            "x-ratelimit-limit-requests", "ratelimit-limit-requests", "anthropic-ratelimit-requests-limit"));
        long? requestsRemaining = ParseNonNegativeInteger(GetFirst(response.Headers,
            "x-ratelimit-remaining-requests", "ratelimit-remaining-requests", "anthropic-ratelimit-requests-remaining"));
        long? tokenLimit = ParseNonNegativeInteger(GetFirst(response.Headers,
            "x-ratelimit-limit-tokens", "ratelimit-limit-tokens", "anthropic-ratelimit-tokens-limit"));
        long? tokensRemaining = ParseNonNegativeInteger(GetFirst(response.Headers,
            "x-ratelimit-remaining-tokens", "ratelimit-remaining-tokens", "anthropic-ratelimit-tokens-remaining"));

        var requestReset = ParseReset(GetFirst(response.Headers,
            "x-ratelimit-reset-requests", "ratelimit-reset-requests", "anthropic-ratelimit-requests-reset"), now);
        var tokenReset = ParseReset(GetFirst(response.Headers,
            "x-ratelimit-reset-tokens", "ratelimit-reset-tokens", "anthropic-ratelimit-tokens-reset"), now);

        string? retryRaw = GetFirst(response.Headers, "retry-after");
        var retryReset = ParseRetryAfter(response.Headers.RetryAfter, retryRaw, now);

        return new UpstreamResponseMetadata
        {
            RequestLimit = requestLimit,
            RequestsRemaining = requestsRemaining,
            TokenLimit = tokenLimit,
            TokensRemaining = tokensRemaining,
            RequestsResetAt = requestReset.At,
            TokensResetAt = tokenReset.At,
            RetryAfterAt = retryReset.At,
            RequestsResetAfter = requestReset.After,
            TokensResetAfter = tokenReset.After,
            RetryAfter = retryReset.After,
            ResponseHeaderLatencyMs = Math.Max(0, responseHeaderLatencyMs)
        };
    }

    private static string? GetFirst(HttpHeaders headers, params string[] names)
    {
        foreach (string name in names)
        {
            if (headers.TryGetValues(name, out var values))
                return values.FirstOrDefault();
        }
        return null;
    }

    private static long? ParseNonNegativeInteger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0)
            return parsed;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal decimalValue)
            && decimalValue >= 0 && decimalValue <= long.MaxValue)
            return (long)decimal.Truncate(decimalValue);
        return null;
    }

    private static (DateTimeOffset? At, TimeSpan? After) ParseRetryAfter(
        RetryConditionHeaderValue? typed,
        string? raw,
        DateTimeOffset now)
    {
        if (typed?.Delta is { } delta)
            return (now + delta, delta);
        if (typed?.Date is { } date)
            return (date, PositiveDifference(date, now));
        return ParseReset(raw, now);
    }

    private static (DateTimeOffset? At, TimeSpan? After) ParseReset(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        string value = raw.Trim();

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date))
        {
            return (date, PositiveDifference(date, now));
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch)
            && epoch >= 1_000_000_000)
        {
            try
            {
                var epochDate = epoch >= 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
                return (epochDate, PositiveDifference(epochDate, now));
            }
            catch (ArgumentOutOfRangeException)
            {
                return (null, null);
            }
        }

        var matches = DurationPartRegex.Matches(value);
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)).Length != value.Length)
            return (null, null);

        double milliseconds = 0;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double amount))
                return (null, null);
            milliseconds += match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "ms" => amount,
                "s" => amount * 1_000,
                "m" => amount * 60_000,
                "h" => amount * 3_600_000,
                "d" => amount * 86_400_000,
                _ => 0
            };
        }

        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            return (null, null);
        var after = TimeSpan.FromMilliseconds(milliseconds);
        return (now + after, after);
    }

    private static TimeSpan? PositiveDifference(DateTimeOffset value, DateTimeOffset now)
    {
        TimeSpan difference = value - now;
        return difference >= TimeSpan.Zero ? difference : TimeSpan.Zero;
    }
}
