using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Groups library songs into albums, enriches them through the online provider
/// layer, and caches the provider result so an album is fetched only once.
///
/// This is intentionally collection-oriented (album now, artist next), so future
/// artist support can reuse the same search/home/detail pattern without changing
/// the library model.
/// </summary>
public sealed class AlbumCatalogService
{
    private readonly LibraryService _library;
    private readonly IAlbumMetadataProvider _online;
    private readonly string _cachePath;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, AlbumCacheEntry> _cache;

    public AlbumCatalogService(
        LibraryService library,
        IAlbumMetadataProvider? online = null,
        string? cachePath = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _online = online ?? new MetadataOnlineService(
            providers: MetadataProviderSettings.FromConfig(ConfigService.Instance.Current));
        _cachePath = cachePath ?? Path.Combine(AppPaths.AppDataDir, "album-cache.json");
        _cache = LoadCache(_cachePath);
    }

    /// <summary>Stable album id from the normalized album title and album artist.</summary>
    public static string AlbumId(string album, string albumArtist, string artist)
    {
        var key = Normalize(album) + "\u001f" +
                  Normalize(string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>Albums that have at least one local, playable track.</summary>
    public List<AlbumInfo> GetAlbums()
    {
        return GroupLocalAlbums()
            .Select(g => BuildInfo(g, CachedEntry(g.Id)))
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Number of albums with at least one present local track.</summary>
    public int CountAlbums() => GroupLocalAlbums().Count;

    /// <summary>
    /// Albums that belong to an artist, either because they are tagged with that
    /// album artist or because one of their tracks credits that artist. Used by
    /// the artist page and kept here so album ids/missing counts stay consistent.
    /// </summary>
    public List<AlbumInfo> AlbumsForArtist(string artist, int maxResults = 100)
    {
        var normalized = Normalize(artist);
        if (string.IsNullOrWhiteSpace(normalized)) return new List<AlbumInfo>();

        return GroupLocalAlbums()
            .Where(g => Normalize(g.AlbumArtist) == normalized ||
                        g.Songs.Any(s => Normalize(s.Artist) == normalized ||
                                         Normalize(s.AlbumArtist) == normalized))
            .OrderByDescending(g => g.Year)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 500))
            .Select(g => BuildInfo(g, CachedEntry(g.Id)))
            .ToList();
    }

    public List<AlbumInfo> Search(string query, int maxResults = 20)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new List<AlbumInfo>();

        // Filter/order the lightweight groups first, then build payloads for the
        // handful that will actually be shown. Building every album would turn
        // every search into thousands of artwork conversions.
        return GroupLocalAlbums()
            .Where(g => terms.All(t =>
                Contains(g.Title, t) || Contains(g.AlbumArtist, t) ||
                Contains(g.Genre, t) || g.Year.ToString().Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(g => g.Songs.Count)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(g => BuildInfo(g, CachedEntry(g.Id)))
            .ToList();
    }

    /// <summary>Albums for the home tab, newest/most recently played first.</summary>
    public List<AlbumInfo> HomeAlbums(int count = 12)
    {
        return GroupLocalAlbums()
            .OrderByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue)
            .ThenByDescending(g => g.DateAddedUtc)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(count, 1, 100))
            .Select(g => BuildInfo(g, CachedEntry(g.Id)))
            .ToList();
    }

    /// <summary>
    /// Album recommendations: song-level recommendation scores are aggregated by
    /// album, with a fallback to the normal home ordering.
    /// </summary>
    public List<AlbumInfo> RecommendedAlbums(int count = 12)
    {
        var data = _library.Data;
        var albums = GroupLocalAlbums();
        if (albums.Count == 0) return new List<AlbumInfo>();

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var recommendation in RecommendationEngine.Recommend(data, 300))
            {
                var albumId = AlbumId(recommendation.Song.Album, recommendation.Song.AlbumArtist, recommendation.Song.Artist);
                scores.TryGetValue(albumId, out var score);
                scores[albumId] = score + recommendation.Score;
                if (!reasons.ContainsKey(albumId) && !string.IsNullOrWhiteSpace(recommendation.Reason))
                    reasons[albumId] = recommendation.Reason;
            }
        }
        catch { }

        return albums
            .OrderByDescending(g => scores.TryGetValue(g.Id, out var score) ? score : 0)
            .ThenByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue)
            .ThenByDescending(g => g.DateAddedUtc)
            .Take(Math.Clamp(count, 1, 100))
            .Select(g =>
            {
                var info = BuildInfo(g, CachedEntry(g.Id));
                info.Score = scores.TryGetValue(info.Id, out var score) ? score : 0;
                info.Reason = reasons.TryGetValue(info.Id, out var reason) ? reason : "From your library";
                return info;
            })
            .ToList();
    }

    /// <summary>
    /// Reads one album, fetching and caching provider metadata/tracks the first time
    /// it is requested. <paramref name="refresh"/> forces a fresh provider lookup.
    /// </summary>
    public async Task<AlbumInfo?> GetAlbumAsync(string albumId, bool refresh = false)
    {
        var group = GroupLocalAlbums().FirstOrDefault(g => g.Id == albumId);
        if (group == null) return null;

        AlbumCacheEntry? cache;
        lock (_cacheLock) _cache.TryGetValue(albumId, out cache);

        if (refresh || cache == null)
        {
            var fetched = await FetchProviderAlbumAsync(group).ConfigureAwait(false);
            if (fetched != null)
            {
                cache = fetched;
                lock (_cacheLock) _cache[albumId] = fetched;
                SaveCache(_cachePath, SnapshotCache());
            }
        }

        return BuildInfo(group, cache);
    }

    public async Task<AlbumInfo?> GetAlbumForSongAsync(string songId, bool refresh = false)
    {
        LibrarySong? song;
        lock (_library.Data) song = _library.Data.FindSong(songId);
        if (song == null || string.IsNullOrWhiteSpace(song.Album)) return null;
        return await GetAlbumAsync(AlbumId(song.Album, song.AlbumArtist, song.Artist), refresh).ConfigureAwait(false);
    }

    /// <summary>
    /// The local, playable songs of an album in track order. Playback uses this
    /// rather than <see cref="GetAlbumAsync"/> so it never waits for a provider.
    /// </summary>
    public List<LibrarySong> GetLocalSongs(string albumId)
    {
        LibrarySong[] songs;
        lock (_library.Data) songs = _library.Data.Songs.ToArray();
        return songs
            .Where(s => s != null && s.FileStatus == FileStatus.Present &&
                        !string.IsNullOrWhiteSpace(s.Album) &&
                        AlbumId(s.Album, s.AlbumArtist, s.Artist) == albumId)
            .OrderBy(s => s.Disc == 0 ? 1 : s.Disc)
            .ThenBy(s => s.Track)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Forgets the cached provider data for the album that contains the song.</summary>
    public void InvalidateForSong(string songId)
    {
        LibrarySong? song;
        lock (_library.Data) song = _library.Data.FindSong(songId);
        if (song == null || string.IsNullOrWhiteSpace(song.Album)) return;

        var id = AlbumId(song.Album, song.AlbumArtist, song.Artist);
        lock (_cacheLock)
        {
            if (_cache.Remove(id))
                SaveCache(_cachePath, SnapshotCache());
        }
    }

    private async Task<AlbumCacheEntry?> FetchProviderAlbumAsync(LocalAlbum group)
    {
        try
        {
            var provider = await _online.FindAlbumAsync(group.Title, group.AlbumArtist, group.Year).ConfigureAwait(false);
            if (provider == null ||
                (string.IsNullOrWhiteSpace(provider.Title) && provider.Tracks.Count == 0))
                return null;

            var entry = new AlbumCacheEntry
            {
                Id = group.Id,
                Title = string.IsNullOrWhiteSpace(provider.Title) ? group.Title : provider.Title,
                Artist = string.IsNullOrWhiteSpace(provider.Artist) ? group.AlbumArtist : provider.Artist,
                Year = provider.Year > 0 ? provider.Year : group.Year,
                Genre = provider.Genre ?? string.Empty,
                ArtworkUrl = provider.ArtworkUrl,
                Provider = provider.Provider,
                FetchedUtc = DateTime.UtcNow,
                Tracks = provider.Tracks.Select(CloneTrack).ToList(),
            };

            var artwork = await _online.FetchArtworkAsync(provider.ArtworkUrl ?? string.Empty).ConfigureAwait(false);
            if (artwork.Bytes is { Length: > 0 })
            {
                try
                {
                    var dir = Path.Combine(AppPaths.AppDataDir, "album-artwork");
                    Directory.CreateDirectory(dir);
                    var ext = (artwork.MimeType ?? "image/jpeg").ToLowerInvariant() switch
                    {
                        "image/png" => ".png",
                        "image/gif" => ".gif",
                        "image/webp" => ".webp",
                        _ => ".jpg",
                    };
                    var path = Path.Combine(dir, group.Id + ext);
                    File.WriteAllBytes(path, artwork.Bytes);
                    entry.ArtworkPath = path;
                }
                catch { }
            }

            return entry;
        }
        catch
        {
            return null;
        }
    }

    private AlbumInfo BuildInfo(LocalAlbum group, AlbumCacheEntry? provider)
    {
        var info = new AlbumInfo
        {
            Id = group.Id,
            Title = provider?.Title is { Length: > 0 } title ? title : group.Title,
            Artist = provider?.Artist is { Length: > 0 } artist ? artist : group.AlbumArtist,
            AlbumArtist = group.AlbumArtist,
            Year = provider?.Year > 0 ? provider.Year : group.Year,
            Genre = !string.IsNullOrWhiteSpace(provider?.Genre) ? provider!.Genre : group.Genre,
            ArtworkPath = provider?.ArtworkPath ?? group.ArtworkPath,
            ArtworkUrl = provider?.ArtworkUrl,
            Provider = provider?.Provider,
            CachedUtc = provider?.FetchedUtc,
            LastPlayedUtc = group.LastPlayedUtc,
            DateAddedUtc = group.DateAddedUtc,
        };

        var localTracks = group.Songs.Select(ToTrack).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (provider?.Tracks is { Count: > 0 } providerTracks)
        {
            foreach (var providerTrack in providerTracks)
            {
                var local = MatchLocal(providerTrack, localTracks, used);
                if (local != null)
                {
                    used.Add(local.SongId ?? local.FilePath ?? local.Title);
                    info.Tracks.Add(local);
                }
                else
                {
                    var missing = CloneTrack(providerTrack);
                    missing.IsLocal = false;
                    missing.IsMissing = true;
                    missing.SongId = null;
                    missing.FilePath = null;
                    info.Tracks.Add(missing);
                }
            }
        }

        // Local-only tracks (e.g. a bonus track the provider does not list) remain visible.
        foreach (var local in localTracks)
        {
            var key = local.SongId ?? local.FilePath ?? local.Title;
            if (used.Contains(key)) continue;
            info.Tracks.Add(local);
        }

        // Library files frequently have no disc tag (0) while provider tracks use
        // 1-based discs. Treat 0 as disc 1 so an untagged local track does not sort
        // before every provider track.
        info.Tracks = info.Tracks
            .OrderBy(t => t.Disc == 0 ? 1 : t.Disc)
            .ThenBy(t => t.Track)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        info.LocalTrackCount = info.Tracks.Count(t => t.IsLocal);
        info.MissingTrackCount = info.Tracks.Count(t => t.IsMissing);
        info.IsComplete = info.MissingTrackCount == 0;
        return info;
    }

    private static AlbumTrackInfo? MatchLocal(AlbumTrackInfo providerTrack, List<AlbumTrackInfo> local, HashSet<string> used)
    {
        var title = Normalize(providerTrack.Title);

        var exact = local.FirstOrDefault(t =>
            !used.Contains(t.SongId ?? t.FilePath ?? t.Title) &&
            Normalize(t.Title) == title &&
            (providerTrack.Track == 0 || t.Track == 0 || providerTrack.Track == t.Track));
        if (exact != null) return exact;

        var byTitle = local.FirstOrDefault(t =>
            !used.Contains(t.SongId ?? t.FilePath ?? t.Title) && Normalize(t.Title) == title);
        if (byTitle != null) return byTitle;

        if (providerTrack.Track > 0)
        {
            var byNumber = local.FirstOrDefault(t =>
                !used.Contains(t.SongId ?? t.FilePath ?? t.Title) &&
                t.Track == providerTrack.Track &&
                (providerTrack.Disc == 0 || t.Disc == 0 || t.Disc == providerTrack.Disc));
            if (byNumber != null) return byNumber;
        }

        return null;
    }

    private List<LocalAlbum> GroupLocalAlbums()
    {
        LibrarySong[] songs;
        lock (_library.Data) songs = _library.Data.Songs.ToArray();

        var groups = new Dictionary<string, List<LibrarySong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Album)) continue;
            if (song.FileStatus != FileStatus.Present) continue;
            var id = AlbumId(song.Album, song.AlbumArtist, song.Artist);
            if (!groups.TryGetValue(id, out var list))
            {
                list = new List<LibrarySong>();
                groups[id] = list;
            }
            list.Add(song);
        }

        return groups.Select(kv =>
        {
            var list = kv.Value;
            var first = list[0];
            return new LocalAlbum
            {
                Id = kv.Key,
                Title = first.Album,
                AlbumArtist = string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.Artist : first.AlbumArtist,
                Genre = list.Select(s => s.Genre).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? string.Empty,
                Year = list.Select(s => s.Year).Where(y => y > 0).DefaultIfEmpty((uint)0).Max(),
                ArtworkPath = list.Select(s => s.ThumbnailPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                Songs = list,
                LastPlayedUtc = list.Select(s => s.LastPlayedUtc).Where(d => d.HasValue).DefaultIfEmpty((DateTime?)null).Max(),
                DateAddedUtc = list.Select(s => s.DateAddedUtc).DefaultIfEmpty(DateTime.MinValue).Max(),
            };
        }).ToList();
    }

    private static AlbumTrackInfo ToTrack(LibrarySong song) => new()
    {
        Title = song.Title,
        Artist = song.Artist,
        Track = song.Track,
        Disc = song.Disc,
        Duration = song.Duration,
        SongId = song.Id,
        FilePath = song.FilePath,
        ThumbnailPath = song.ThumbnailPath,
        IsLocal = true,
        IsMissing = false,
    };

    private static AlbumTrackInfo CloneTrack(AlbumTrackInfo track) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        Track = track.Track,
        Disc = track.Disc,
        Duration = track.Duration,
        SongId = track.SongId,
        FilePath = track.FilePath,
        ThumbnailPath = track.ThumbnailPath,
        IsLocal = track.IsLocal,
        IsMissing = track.IsMissing,
    };

    private AlbumCacheEntry? CachedEntry(string albumId)
    {
        lock (_cacheLock) return _cache.TryGetValue(albumId, out var entry) ? entry : null;
    }

    private List<AlbumCacheEntry> SnapshotCache()
    {
        lock (_cacheLock) return _cache.Values.ToList();
    }

    private static Dictionary<string, AlbumCacheEntry> LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, AlbumCacheEntry>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            var list = JsonConvert.DeserializeObject<List<AlbumCacheEntry>>(json) ?? new();
            return list
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, AlbumCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveCache(string path, List<AlbumCacheEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch { }
    }

    private static bool Contains(string? value, string term)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text;
    }

    private sealed class LocalAlbum
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public uint Year { get; set; }
        public string? ArtworkPath { get; set; }
        public List<LibrarySong> Songs { get; set; } = new();
        public DateTime? LastPlayedUtc { get; set; }
        public DateTime DateAddedUtc { get; set; }
    }
}
