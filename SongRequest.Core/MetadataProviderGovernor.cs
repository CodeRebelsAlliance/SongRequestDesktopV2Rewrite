using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Per-provider request governor.
///
/// Every online metadata/artwork request passes through here. It enforces a
/// conservative sliding-window limit (below the published provider limits), a
/// minimum interval between requests, and a cooldown/backoff after HTTP errors
/// or rate-limit responses. The metadata editor and player settings show the
/// live state so a cooling provider is visible instead of silently slowing down
/// a search.
/// </summary>
public static class MetadataProviderGovernor
{
    public sealed class ProviderStatus
    {
        public string Name { get; init; } = string.Empty;
        public string State { get; init; } = "ready";
        public bool Available { get; init; }
        public int RequestsInWindow { get; init; }
        public int MaxRequests { get; init; }
        public double WindowSeconds { get; init; }
        public double MinIntervalSeconds { get; init; }
        public DateTime? LastRequestUtc { get; init; }
        public DateTime? NextAllowedUtc { get; init; }
        public DateTime? CooldownUntilUtc { get; init; }
        public string? LastError { get; init; }
        public string LimitLabel { get; init; } = string.Empty;
    }

    private sealed record Limit(int MaxRequests, TimeSpan Window, TimeSpan MinInterval, string Label);

    private sealed class ProviderState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Queue<DateTime> Requests { get; } = new();
        public DateTime NextAllowedUtc { get; set; } = DateTime.MinValue;
        public DateTime CooldownUntilUtc { get; set; } = DateTime.MinValue;
        public DateTime? LastRequestUtc { get; set; }
        public string State { get; set; } = "ready";
        public string? LastError { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    public static readonly IReadOnlyList<string> ProviderNames = new[]
    {
        "MusicBrainz", "iTunes", "Deezer", "CoverArtArchive", "AcoustID", "Discogs"
    };

    private static readonly ConcurrentDictionary<string, ProviderState> States =
        new(StringComparer.OrdinalIgnoreCase);

    // Deliberately below the published limits. The provider only cares about
    // aggregate traffic, and a small margin prevents clock/edge-case violations.
    private static readonly Dictionary<string, Limit> Limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MusicBrainz"] = new(1, TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(1.1), "1 request / 1.1 s"),
        ["iTunes"] = new(18, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3.4), "18 requests / minute"),
        ["Deezer"] = new(40, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(130), "40 requests / 5 s"),
        ["CoverArtArchive"] = new(55, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1.0), "55 requests / minute (polite)"),
        ["AcoustID"] = new(2, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(550), "2 requests / second"),
        ["Discogs"] = new(20, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3.0), "20 requests / minute (unauthenticated)"),
        ["DiscogsAuth"] = new(50, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1.3), "50 requests / minute (authenticated)"),
        ["Artwork"] = new(60, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(0.5), "60 artwork requests / minute"),
    };

    private static Limit LimitFor(string provider, bool authenticated)
        => provider.Equals("Discogs", StringComparison.OrdinalIgnoreCase) && authenticated
            ? Limits["DiscogsAuth"]
            : Limits.TryGetValue(provider, out var limit)
                ? limit
                : new Limit(30, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2), "30 requests / minute");

    private static ProviderState StateFor(string provider)
        => States.GetOrAdd(provider, _ => new ProviderState());

    /// <summary>
    /// Waits until the provider may be called. Returns false while the provider
    /// is cooling down after an error/429, so a search can skip it instead of
    /// blocking the UI for minutes.
    /// </summary>
    public static async Task<bool> AcquireAsync(string provider, bool authenticated, CancellationToken ct)
    {
        var state = StateFor(provider);
        var limit = LimitFor(provider, authenticated);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (state)
                {
                    var now = DateTime.UtcNow;
                    if (state.CooldownUntilUtc > now)
                    {
                        var remaining = state.CooldownUntilUtc - now;
                        if (remaining > TimeSpan.FromSeconds(3))
                            return false;
                        wait = remaining;
                    }
                    else
                    {
                        while (state.Requests.Count > 0 &&
                               now - state.Requests.Peek() >= limit.Window)
                            state.Requests.Dequeue();

                        if (state.Requests.Count >= limit.MaxRequests)
                        {
                            wait = limit.Window - (now - state.Requests.Peek());
                        }
                        else if (state.NextAllowedUtc > now)
                        {
                            wait = state.NextAllowedUtc - now;
                        }
                        else
                        {
                            state.Requests.Enqueue(now);
                            state.LastRequestUtc = now;
                            state.NextAllowedUtc = now + limit.MinInterval;
                            state.State = "ready";
                            state.LastError = null;
                            return true;
                        }
                    }
                }

                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                else
                    await Task.Yield();
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Records a provider response and enters a cooldown on throttling/errors.</summary>
    public static void ReportResponse(string provider, int statusCode, TimeSpan? retryAfter = null)
    {
        var state = StateFor(provider);
        lock (state)
        {
            var now = DateTime.UtcNow;
            if (statusCode is >= 200 and < 300)
            {
                state.State = "ready";
                state.ConsecutiveFailures = 0;
                state.LastError = null;
                state.CooldownUntilUtc = DateTime.MinValue;
                return;
            }

            if (statusCode == 429)
            {
                state.State = "rate_limited";
                state.ConsecutiveFailures++;
                state.LastError = "The provider asked us to slow down (HTTP 429).";
                state.CooldownUntilUtc = now + (retryAfter ?? TimeSpan.FromSeconds(60));
                return;
            }

            if (statusCode is 401 or 403)
            {
                state.State = "unauthorized";
                state.ConsecutiveFailures++;
                state.LastError = statusCode == 401
                    ? "The provider rejected the credentials (HTTP 401)."
                    : "The provider refused the request (HTTP 403); a token or User-Agent may be missing.";
                state.CooldownUntilUtc = now + (retryAfter ?? TimeSpan.FromMinutes(10));
                return;
            }

            if (statusCode >= 500)
            {
                state.State = "error";
                state.ConsecutiveFailures++;
                state.LastError = "The provider returned HTTP " + statusCode + ".";
                var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(4, state.ConsecutiveFailures - 1)));
                state.CooldownUntilUtc = now + (retryAfter ?? backoff);
                return;
            }

            state.State = "error";
            state.ConsecutiveFailures++;
            state.LastError = "The provider returned HTTP " + statusCode + ".";
            state.CooldownUntilUtc = now + (retryAfter ?? TimeSpan.FromSeconds(30));
        }
    }

    public static void ReportException(string provider, Exception exception)
    {
        var state = StateFor(provider);
        lock (state)
        {
            state.State = "error";
            state.ConsecutiveFailures++;
            state.LastError = exception.Message;
            var backoff = TimeSpan.FromSeconds(15 * Math.Pow(2, Math.Min(4, state.ConsecutiveFailures - 1)));
            state.CooldownUntilUtc = DateTime.UtcNow + backoff;
        }
    }

    /// <summary>Returns a snapshot for every known provider (including cooling/error state).</summary>
    public static IReadOnlyList<ProviderStatus> GetStatuses()
    {
        var now = DateTime.UtcNow;
        var list = new List<ProviderStatus>();
        foreach (var name in ProviderNames)
        {
            var state = StateFor(name);
            var limit = LimitFor(name, authenticated: false);
            lock (state)
            {
                while (state.Requests.Count > 0 && now - state.Requests.Peek() >= limit.Window)
                    state.Requests.Dequeue();

                var cooling = state.CooldownUntilUtc > now;
                var available = !cooling && state.Requests.Count < limit.MaxRequests;
                list.Add(new ProviderStatus
                {
                    Name = name,
                    State = cooling ? state.State : "ready",
                    Available = available,
                    RequestsInWindow = state.Requests.Count,
                    MaxRequests = limit.MaxRequests,
                    WindowSeconds = limit.Window.TotalSeconds,
                    MinIntervalSeconds = limit.MinInterval.TotalSeconds,
                    LastRequestUtc = state.LastRequestUtc,
                    NextAllowedUtc = state.NextAllowedUtc > now ? state.NextAllowedUtc : null,
                    CooldownUntilUtc = cooling ? state.CooldownUntilUtc : null,
                    LastError = state.LastError,
                    LimitLabel = limit.Label,
                });
            }
        }

        return list;
    }

    /// <summary>Clears a provider's cooldown/error state without forgetting its recent requests.</summary>
    public static void Reset(string provider)
    {
        var state = StateFor(provider);
        lock (state)
        {
            state.State = "ready";
            state.LastError = null;
            state.ConsecutiveFailures = 0;
            state.CooldownUntilUtc = DateTime.MinValue;
        }
    }

    /// <summary>Test-only full reset (also forgets the sliding window).</summary>
    internal static void ResetForTests(string provider)
    {
        var state = StateFor(provider);
        lock (state)
        {
            state.Requests.Clear();
            state.State = "ready";
            state.LastError = null;
            state.ConsecutiveFailures = 0;
            state.CooldownUntilUtc = DateTime.MinValue;
            state.NextAllowedUtc = DateTime.MinValue;
            state.LastRequestUtc = null;
        }
    }
}
