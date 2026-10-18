using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The library recommendation engine described in <c>docs/library-data-models.md</c>.
///
/// It scores every playable library song with five signals and returns the best ones
/// with a human-readable reason:
///
/// <list type="table">
///   <item><term>artist_affinity (30%)</term><description>How much the listener likes that artist, from the play history.</description></item>
///   <item><term>genre_affinity (20%)</term><description>Same, per genre.</description></item>
///   <item><term>fresh_discovery (25%)</term><description>Never played + good metadata.</description></item>
///   <item><term>recently_enjoyed (15%)</term><description>Similarity to what was played recently.</description></item>
///   <item><term>collaborative (10%)</term><description>Reserved for server-side similar-listener data; always 0 for now.</description></item>
/// </list>
///
/// Plays the engine itself suggested (<see cref="PlayHistoryEntry.Trigger"/> =
/// <c>"recommendation"</c>) are discounted so it does not keep pushing the same songs.
/// Only library-indexed songs with a playable file are ever suggested.
/// </summary>
public static class RecommendationEngine
{
    // Signal weights — keep in sync with docs/library-data-models.md.
    public const double ArtistAffinityWeight = 0.30;
    public const double GenreAffinityWeight = 0.20;
    public const double FreshDiscoveryWeight = 0.25;
    public const double RecentlyEnjoyedWeight = 0.15;
    public const double CollaborativeWeight = 0.10;

    /// <summary>Half-life (days) of a play's influence on affinity.</summary>
    private const double AffinityHalfLifeDays = 14.0;

    /// <summary>How many recent plays feed the "recently enjoyed" similarity.</summary>
    private const int RecentSeedCount = 15;

    /// <summary>How many of the most recently played songs are kept out of the suggestions.</summary>
    private const int ExcludeRecentlyPlayedCount = 3;

    /// <summary>Discount applied to songs the engine suggested and the listener skipped.</summary>
    private const double EchoChamberDiscount = 0.5;

    public sealed record ScoredSong(LibrarySong Song, double Score, string Reason, string Signal);

    /// <summary>
    /// Scores the library and returns the best <paramref name="count"/> suggestions,
    /// highest score first.
    /// </summary>
    /// <param name="data">The library (songs + play history).</param>
    /// <param name="count">Maximum number of suggestions.</param>
    /// <param name="nowUtc">"Now" — injectable so the affinity decay is testable.</param>
    public static List<ScoredSong> Recommend(LibraryData data, int count = 20, DateTime? nowUtc = null)
    {
        var result = new List<ScoredSong>();
        if (data == null || count <= 0) return result;

        var now = nowUtc ?? DateTime.UtcNow;

        // Only library-indexed, playable songs can be suggested.
        var candidates = data.Songs
            .Where(s => s != null && s.FileStatus == FileStatus.Present && !string.IsNullOrEmpty(s.FilePath))
            .ToList();
        if (candidates.Count == 0) return result;

        var byId = candidates.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
        var history = data.PlayHistory ?? new List<PlayHistoryEntry>();

        var artistAffinity = BuildAffinity(history, byId, now, s => Normalize(s.Artist));
        var genreAffinity = BuildAffinity(history, byId, now, s => Normalize(s.Genre));

        // Seed songs for the similarity signal: what was played recently.
        var recentSeeds = history
            .OrderByDescending(e => e.PlayedUtc)
            .Select(e => byId.TryGetValue(e.SongId, out var s) ? s : null)
            .Where(s => s != null)
            .Take(RecentSeedCount)
            .Select(s => s!)
            .ToList();

        var recentlyPlayedIds = new HashSet<string>(
            candidates
                .Where(s => s.LastPlayedUtc.HasValue)
                .OrderByDescending(s => s.LastPlayedUtc!.Value)
                .Take(ExcludeRecentlyPlayedCount)
                .Select(s => s.Id),
            StringComparer.OrdinalIgnoreCase);

        // Per-song reaction to the engine's own past suggestions.
        var suggestedIds = new HashSet<string>(
            history.Where(e => IsRecommendationTrigger(e.Trigger)).Select(e => e.SongId),
            StringComparer.OrdinalIgnoreCase);
        var skippedSuggestedIds = new HashSet<string>(
            history.Where(e => IsRecommendationTrigger(e.Trigger) && !e.Completed)
                   .GroupBy(e => e.SongId, StringComparer.OrdinalIgnoreCase)
                   .Where(g => g.All(e => !e.Completed))
                   .Select(g => g.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var song in candidates)
        {
            if (recentlyPlayedIds.Contains(song.Id)) continue;

            double artist = Lookup(artistAffinity, Normalize(song.Artist));
            double genre = Lookup(genreAffinity, Normalize(song.Genre));
            double fresh = Freshness(song);
            var (recent, seed) = RecentlyEnjoyed(song, recentSeeds);
            const double collaborative = 0.0;   // reserved for server-side data

            double score =
                ArtistAffinityWeight * artist +
                GenreAffinityWeight * genre +
                FreshDiscoveryWeight * fresh +
                RecentlyEnjoyedWeight * recent +
                CollaborativeWeight * collaborative;

            if (skippedSuggestedIds.Contains(song.Id)) score *= EchoChamberDiscount;
            else if (suggestedIds.Contains(song.Id)) score *= (EchoChamberDiscount + 1) / 2;

            if (score <= 0) continue;

            var (signal, reason) = Explain(song, artist, genre, fresh, recent, seed);
            result.Add(new ScoredSong(song, Clamp01(score), reason, signal));
        }

        return result
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Song.DateAddedUtc)
            .ThenBy(r => r.Song.Title, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
    }

    // ──────────────────────────────────────────────────────────────
    //  Signals
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Recency-weighted share of plays per key (artist or genre), normalised so the
    /// listener's current favourite is 1.0. Completions count for more than skips, and
    /// the engine's own suggestions count for less (no echo chamber).
    /// </summary>
    private static Dictionary<string, double> BuildAffinity(
        List<PlayHistoryEntry> history,
        Dictionary<string, LibrarySong> byId,
        DateTime now,
        Func<LibrarySong, string> keySelector)
    {
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in history)
        {
            if (entry == null) continue;
            if (!byId.TryGetValue(entry.SongId, out var song)) continue;

            var key = keySelector(song);
            if (string.IsNullOrEmpty(key)) continue;

            double decay = Math.Pow(0.5, Math.Max(0, (now - entry.PlayedUtc).TotalDays) / AffinityHalfLifeDays);
            double weight = entry.Completed ? 1.0 : 0.6;
            if (IsRecommendationTrigger(entry.Trigger)) weight *= 0.7;

            sums.TryGetValue(key, out double current);
            sums[key] = current + decay * weight;
        }

        if (sums.Count == 0) return sums;

        double max = sums.Values.Max();
        if (max <= 0) return sums;

        foreach (var key in sums.Keys.ToList()) sums[key] /= max;
        return sums;
    }

    /// <summary>
    /// "Fresh discovery": never played, with complete metadata. A song with a title,
    /// artist, album, genre and duration is worth the full 25%.
    /// </summary>
    private static double Freshness(LibrarySong song)
    {
        if (song.PlayCount > 0) return 0.0;

        int filled = 0;
        if (!string.IsNullOrWhiteSpace(song.Title)) filled++;
        if (!string.IsNullOrWhiteSpace(song.Artist) && !string.Equals(song.Artist, "Unknown", StringComparison.OrdinalIgnoreCase)) filled++;
        if (!string.IsNullOrWhiteSpace(song.Album)) filled++;
        if (!string.IsNullOrWhiteSpace(song.Genre)) filled++;
        if (song.Duration > TimeSpan.Zero) filled++;
        if (!string.IsNullOrWhiteSpace(song.ThumbnailPath)) filled++;

        // 1 of 6 fields is enough to be suggestable, all 6 gives the full weight.
        const int expectedFields = 5;   // thumbnail is a bonus, not a requirement
        return Math.Clamp(filled / (double)expectedFields, 0.2, 1.0);
    }

    /// <summary>
    /// Cosine similarity between the song and the recently played tracks, over the
    /// artist/genre/album/tag feature space. Returns the best match and its seed song.
    /// </summary>
    private static (double Score, LibrarySong? Seed) RecentlyEnjoyed(LibrarySong song, List<LibrarySong> seeds)
    {
        if (seeds.Count == 0) return (0, null);

        var songVector = Features(song);
        double best = 0;
        LibrarySong? bestSeed = null;

        foreach (var seed in seeds)
        {
            if (string.Equals(seed.Id, song.Id, StringComparison.OrdinalIgnoreCase)) continue;

            var seedVector = Features(seed);
            double dot = 0, a = 0, b = 0;
            foreach (var kv in songVector)
            {
                a += kv.Value * kv.Value;
                if (seedVector.TryGetValue(kv.Key, out double other)) dot += kv.Value * other;
            }
            foreach (var kv in seedVector) b += kv.Value * kv.Value;

            double denom = Math.Sqrt(a) * Math.Sqrt(b);
            if (denom <= 0) continue;

            double similarity = dot / denom;
            if (similarity > best)
            {
                best = similarity;
                bestSeed = seed;
            }
        }

        return (Clamp01(best), bestSeed);
    }

    /// <summary>Sparse feature vector: artist, genre, album and tags.</summary>
    private static Dictionary<string, double> Features(LibrarySong song)
    {
        var features = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        void Add(string prefix, string? value, double weight)
        {
            var v = Normalize(value);
            if (string.IsNullOrEmpty(v)) return;
            features[prefix + ":" + v] = weight;
        }

        Add("artist", song.Artist, 1.0);
        Add("genre", song.Genre, 0.8);
        Add("album", song.Album, 0.6);
        if (song.Tags != null)
            foreach (var tag in song.Tags) Add("tag", tag, 0.9);

        return features;
    }

    /// <summary>Dominant signal + a reason the UI can show.</summary>
    private static (string Signal, string Reason) Explain(
        LibrarySong song, double artist, double genre, double fresh, double recent, LibrarySong? seed)
    {
        var contributions = new (string Signal, double Value, string Reason)[]
        {
            ("artist_affinity", ArtistAffinityWeight * artist,
                string.IsNullOrWhiteSpace(song.Artist) || string.Equals(song.Artist, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "An artist you listen to"
                    : $"More from {song.Artist}"),
            ("genre_affinity", GenreAffinityWeight * genre,
                string.IsNullOrWhiteSpace(song.Genre) ? "A genre you listen to" : $"More {song.Genre}"),
            ("fresh_discovery", FreshDiscoveryWeight * fresh, "New in your library"),
            ("recently_enjoyed", RecentlyEnjoyedWeight * recent,
                seed != null ? $"Because you played {seed.Title}" : "Similar to your recent listening"),
            ("collaborative", 0, "Popular with listeners like you"),
        };

        var best = contributions.OrderByDescending(c => c.Value).First();
        return (best.Signal, best.Reason);
    }

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private static bool IsRecommendationTrigger(string? trigger)
        => string.Equals(trigger, "recommendation", StringComparison.OrdinalIgnoreCase);

    private static double Lookup(Dictionary<string, double> table, string key)
        => !string.IsNullOrEmpty(key) && table.TryGetValue(key, out double value) ? value : 0.0;

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0.0, 1.0);
}
