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
/// Groups library songs by artist, enriches the artist through the online
/// provider layer, and caches the provider result so it is fetched only once.
///
/// This mirrors <see cref="AlbumCatalogService"/>: local grouping, search and
/// recommendations are live; only provider metadata/artwork is cached.
/// </summary>
public sealed class ArtistCatalogService
{
    private readonly LibraryService _library;
    private readonly AlbumCatalogService _albums;
    private readonly IArtistMetadataProvider _online;
    private readonly string _cachePath;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ArtistCacheEntry> _cache;

    public ArtistCatalogService(
        LibraryService library,
        AlbumCatalogService? albums = null,
        IArtistMetadataProvider? online = null,
        string? cachePath = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _albums = albums ?? new AlbumCatalogService(library);
        _online = online ?? new MetadataOnlineService(
            providers: MetadataProviderSettings.FromConfig(ConfigService.Instance.Current));
        _cachePath = cachePath ?? Path.Combine(AppPaths.AppDataDir, "artist-cache.json");
        _cache = LoadCache(_cachePath);
    }

    /// <summary>Stable artist id from the normalized display name.</summary>
    public static string ArtistId(string artist)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(artist)));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>Artists that have at least one present, playable track.</summary>
    public List<ArtistInfo> GetArtists()
    {
        return GroupLocalArtists()
            .Select(g => BuildInfo(g, CachedEntry(g.Id), includeDetails: false))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int CountArtists() => GroupLocalArtists().Count;

    public List<ArtistInfo> Search(string query, int maxResults = 20)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return new List<ArtistInfo>();

        return GroupLocalArtists()
            .Where(g => terms.All(t => Contains(g.Name, t) || Contains(g.Genre, t)))
            .OrderByDescending(g => g.Songs.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(g => BuildInfo(g, CachedEntry(g.Id), includeDetails: false))
            .ToList();
    }

    /// <summary>Artists for the home tab, recently played/added first.</summary>
    public List<ArtistInfo> HomeArtists(int count = 12)
    {
        return GroupLocalArtists()
            .OrderByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue)
            .ThenByDescending(g => g.DateAddedUtc)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(count, 1, 100))
            .Select(g => BuildInfo(g, CachedEntry(g.Id), includeDetails: false))
            .ToList();
    }

    /// <summary>
    /// Artist recommendations: song-level recommendation scores are aggregated
    /// by artist, falling back to the normal home ordering.
    /// </summary>
    public List<ArtistInfo> RecommendedArtists(int count = 12)
    {
        var data = _library.Data;
        var artists = GroupLocalArtists();
        if (artists.Count == 0) return new List<ArtistInfo>();

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var recommendation in RecommendationEngine.Recommend(data, 300))
            {
                var name = EffectiveArtist(recommendation.Song);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var artistId = ArtistId(name);
                scores.TryGetValue(artistId, out var score);
                scores[artistId] = score + recommendation.Score;
                if (!reasons.ContainsKey(artistId) && !string.IsNullOrWhiteSpace(recommendation.Reason))
                    reasons[artistId] = recommendation.Reason;
            }
        }
        catch { }

        return artists
            .OrderByDescending(g => scores.TryGetValue(g.Id, out var score) ? score : 0)
            .ThenByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue)
            .ThenByDescending(g => g.DateAddedUtc)
            .Take(Math.Clamp(count, 1, 100))
            .Select(g =>
            {
                var info = BuildInfo(g, CachedEntry(g.Id), includeDetails: false);
                info.Score = scores.TryGetValue(info.Id, out var score) ? score : 0;
                info.Reason = reasons.TryGetValue(info.Id, out var reason) ? reason : "From your library";
                return info;
            })
            .ToList();
    }

    /// <summary>
    /// Reads one artist, fetching and caching provider metadata/artwork the first
    /// time it is requested. <paramref name="refresh"/> forces a fresh lookup.
    /// </summary>
    public async Task<ArtistInfo?> GetArtistAsync(string artistId, bool refresh = false)
    {
        var group = GroupLocalArtists().FirstOrDefault(g => g.Id == artistId);
        if (group == null) return null;

        var cache = CachedEntry(artistId);
        if (refresh || cache == null)
        {
            var fetched = await FetchProviderArtistAsync(group).ConfigureAwait(false);
            if (fetched != null)
            {
                cache = fetched;
                lock (_cacheLock) _cache[artistId] = fetched;
                SaveCache(_cachePath, SnapshotCache());
            }
        }

        return BuildInfo(group, cache, includeDetails: true);
    }

    public async Task<ArtistInfo?> GetArtistForSongAsync(string songId, bool refresh = false)
    {
        LibrarySong? song;
        lock (_library.Data) song = _library.Data.FindSong(songId);
        if (song == null) return null;

        var artist = EffectiveArtist(song);
        if (string.IsNullOrWhiteSpace(artist)) return null;
        return await GetArtistAsync(ArtistId(artist), refresh).ConfigureAwait(false);
    }

    /// <summary>
    /// The local, playable songs of an artist in album/track order. Playback uses
    /// this rather than <see cref="GetArtistAsync"/> so it never waits for a provider.
    /// </summary>
    public List<LibrarySong> GetLocalSongs(string artistId)
    {
        LibrarySong[] songs;
        lock (_library.Data) songs = _library.Data.Songs.ToArray();
        return songs
            .Where(s => s != null && s.FileStatus == FileStatus.Present &&
                        !string.IsNullOrWhiteSpace(EffectiveArtist(s)) &&
                        ArtistId(EffectiveArtist(s)) == artistId)
            .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Disc == 0 ? 1 : s.Disc)
            .ThenBy(s => s.Track)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Forgets the cached provider data for the artist that owns the song.</summary>
    public void InvalidateForSong(string songId)
    {
        LibrarySong? song;
        lock (_library.Data) song = _library.Data.FindSong(songId);
        if (song == null) return;

        var artist = EffectiveArtist(song);
        if (string.IsNullOrWhiteSpace(artist)) return;

        lock (_cacheLock)
        {
            if (_cache.Remove(ArtistId(artist)))
                SaveCache(_cachePath, SnapshotCache());
        }
    }

    private async Task<ArtistCacheEntry?> FetchProviderArtistAsync(LocalArtist group)
    {
        try
        {
            var provider = await _online.FindArtistAsync(group.Name).ConfigureAwait(false);
            if (provider == null ||
                (string.IsNullOrWhiteSpace(provider.Name) && string.IsNullOrWhiteSpace(provider.ArtworkUrl)))
                return null;

            var entry = new ArtistCacheEntry
            {
                Id = group.Id,
                Name = string.IsNullOrWhiteSpace(provider.Name) ? group.Name : provider.Name,
                SortName = provider.SortName ?? string.Empty,
                Genre = string.IsNullOrWhiteSpace(provider.Genre) ? group.Genre : provider.Genre,
                Description = provider.Description ?? string.Empty,
                Country = provider.Country ?? string.Empty,
                ArtworkUrl = provider.ArtworkUrl,
                Provider = provider.Provider,
                FetchedUtc = DateTime.UtcNow,
                Albums = provider.Albums.Select(CloneProviderAlbum).ToList(),
            };

            if (!string.IsNullOrWhiteSpace(provider.ArtworkUrl))
            {
                var artwork = await _online.FetchArtworkAsync(provider.ArtworkUrl).ConfigureAwait(false);
                if (artwork.Bytes is { Length: > 0 })
                {
                    try
                    {
                        var dir = Path.Combine(AppPaths.AppDataDir, "artist-artwork");
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
            }

            return entry;
        }
        catch
        {
            return null;
        }
    }

    private ArtistInfo BuildInfo(LocalArtist group, ArtistCacheEntry? provider, bool includeDetails)
    {
        var name = provider?.Name is { Length: > 0 } providerName ? providerName : group.Name;
        var info = new ArtistInfo
        {
            Id = group.Id,
            Name = name,
            SortName = provider?.SortName is { Length: > 0 } sortName ? sortName : name,
            Genre = !string.IsNullOrWhiteSpace(provider?.Genre) ? provider!.Genre : group.Genre,
            Description = provider?.Description ?? string.Empty,
            Country = provider?.Country ?? string.Empty,
            ArtworkPath = provider?.ArtworkPath ?? group.ArtworkPath,
            ArtworkUrl = provider?.ArtworkUrl,
            Provider = provider?.Provider,
            CachedUtc = provider?.FetchedUtc,
            LastPlayedUtc = group.LastPlayedUtc,
            DateAddedUtc = group.DateAddedUtc,
        };

        info.PlayCount = group.Songs.Sum(s => s.PlayCount);

        if (!includeDetails)
        {
            // Home/search summaries only need counts; building every track and
            // scanning the album catalogue per artist would be wasted work.
            info.LocalTrackCount = group.Songs.Count;
            info.AlbumCount = group.Songs
                .Where(s => !string.IsNullOrWhiteSpace(s.Album))
                .Select(s => AlbumCatalogService.AlbumId(s.Album, s.AlbumArtist, s.Artist))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return info;
        }

        info.Tracks = group.Songs
            .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Disc == 0 ? 1 : s.Disc)
            .ThenBy(s => s.Track)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToTrack)
            .ToList();

        // Album ids come from AlbumCatalogService so album cards on the artist
        // page open the exact same album (and share its provider cache).
        var albums = _albums.AlbumsForArtist(group.Name);
        if (albums.Count == 0 && !string.Equals(name, group.Name, StringComparison.OrdinalIgnoreCase))
            albums = _albums.AlbumsForArtist(name);
        info.Albums = albums;
        info.AlbumCount = albums.Count;
        info.CatalogAlbums = BuildCatalogAlbums(albums, provider);
        info.MissingAlbumCount = info.CatalogAlbums.Count(a => !a.IsLocal);
        info.LocalTrackCount = info.Tracks.Count;
        return info;
    }

    private List<LocalArtist> GroupLocalArtists()
    {
        LibrarySong[] songs;
        lock (_library.Data) songs = _library.Data.Songs.ToArray();

        var groups = new Dictionary<string, List<LibrarySong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
        {
            if (song == null || song.FileStatus != FileStatus.Present) continue;
            var artist = EffectiveArtist(song);
            if (string.IsNullOrWhiteSpace(artist)) continue;

            var id = ArtistId(artist);
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
            var name = EffectiveArtist(list[0]);
            return new LocalArtist
            {
                Id = kv.Key,
                Name = name,
                Genre = list.Select(s => s.Genre).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? string.Empty,
                ArtworkPath = list.Select(s => s.ThumbnailPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                Songs = list,
                LastPlayedUtc = list.Select(s => s.LastPlayedUtc).Where(d => d.HasValue).DefaultIfEmpty((DateTime?)null).Max(),
                DateAddedUtc = list.Select(s => s.DateAddedUtc).DefaultIfEmpty(DateTime.MinValue).Max(),
            };
        }).ToList();
    }

    /// <summary>
    /// Merges local albums with the cached provider discography. Provider albums
    /// that already exist locally are dropped; the rest stay as non-local cards
    /// so the artist page can show them greyed out.
    /// </summary>
    private static List<ArtistAlbumInfo> BuildCatalogAlbums(
        List<AlbumInfo> localAlbums,
        ArtistCacheEntry? provider)
    {
        var result = localAlbums.Select(a => new ArtistAlbumInfo
        {
            Id = a.Id,
            Title = a.Title,
            Year = a.Year,
            ArtworkPath = a.ArtworkPath,
            ArtworkUrl = a.ArtworkUrl,
            Provider = a.Provider,
            IsLocal = true,
            LocalTrackCount = a.LocalTrackCount,
            MissingTrackCount = a.MissingTrackCount,
            IsComplete = a.IsComplete,
        }).ToList();

        var seen = new HashSet<string>(
            localAlbums.Select(a => NormalizeAlbumTitle(a.Title)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var album in provider?.Albums ?? new List<ArtistProviderAlbum>())
        {
            if (string.IsNullOrWhiteSpace(album.Title)) continue;
            if (localAlbums.Any(local => AlbumTitlesMatch(local.Title, album.Title))) continue;

            var key = NormalizeAlbumTitle(album.Title);
            if (key.Length == 0 || !seen.Add(key)) continue;

            result.Add(new ArtistAlbumInfo
            {
                Id = "provider:" + AlbumCatalogService.AlbumId(album.Title, provider?.Name ?? string.Empty, string.Empty),
                Title = album.Title,
                Year = album.Year,
                ArtworkUrl = album.ArtworkUrl,
                Provider = album.Provider ?? provider?.Provider,
                IsLocal = false,
                IsComplete = true,
            });
        }

        return result
            .OrderByDescending(a => a.IsLocal)
            .ThenByDescending(a => a.Year)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool AlbumTitlesMatch(string? left, string? right)
    {
        var a = NormalizeAlbumTitle(left);
        var b = NormalizeAlbumTitle(right);
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length < 4 || b.Length < 4) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAlbumTitle(string? value)
    {
        var text = Normalize(value);
        var cut = text.IndexOf(" (", StringComparison.Ordinal);
        if (cut < 0) cut = text.IndexOf(" [", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut];
        cut = text.IndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut];
        return text.Trim();
    }

    private static ArtistProviderAlbum CloneProviderAlbum(ArtistProviderAlbum album) => new()
    {
        Title = album.Title,
        Year = album.Year,
        ArtworkUrl = album.ArtworkUrl,
        Provider = album.Provider,
    };

    private static ArtistTrackInfo ToTrack(LibrarySong song) => new()
    {
        Title = song.Title,
        Artist = EffectiveArtist(song),
        Album = song.Album,
        Year = song.Year,
        Track = song.Track,
        Disc = song.Disc,
        Duration = song.Duration,
        SongId = song.Id,
        FilePath = song.FilePath,
        ThumbnailPath = song.ThumbnailPath,
        PlayCount = song.PlayCount,
        IsMissing = false,
    };

    private ArtistCacheEntry? CachedEntry(string artistId)
    {
        lock (_cacheLock) return _cache.TryGetValue(artistId, out var entry) ? entry : null;
    }

    private List<ArtistCacheEntry> SnapshotCache()
    {
        lock (_cacheLock) return _cache.Values.ToList();
    }

    private static Dictionary<string, ArtistCacheEntry> LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, ArtistCacheEntry>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            var list = JsonConvert.DeserializeObject<List<ArtistCacheEntry>>(json) ?? new();
            return list
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ArtistCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveCache(string path, List<ArtistCacheEntry> entries)
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

    private static string EffectiveArtist(LibrarySong? song)
    {
        if (song == null) return string.Empty;
        return !string.IsNullOrWhiteSpace(song.Artist) ? song.Artist.Trim() : (song.AlbumArtist ?? string.Empty).Trim();
    }

    private static bool Contains(string? value, string term)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class LocalArtist
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string? ArtworkPath { get; set; }
        public List<LibrarySong> Songs { get; set; } = new();
        public DateTime? LastPlayedUtc { get; set; }
        public DateTime DateAddedUtc { get; set; }
    }
}
