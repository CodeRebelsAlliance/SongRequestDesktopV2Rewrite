using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The slice of the online metadata layer the album catalogue needs. Keeping it
/// behind an interface lets album grouping, caching and track matching be tested
/// without any network calls (and leaves room for artist support later).
/// </summary>
public interface IAlbumMetadataProvider
{
    Task<AlbumProviderResult> FindAlbumAsync(
        string album,
        string artist,
        uint year,
        CancellationToken ct = default);

    Task<ArtworkResult> FetchArtworkAsync(string url, CancellationToken ct = default);
}

/// <summary>A track inside an album. Non-local tracks are provider placeholders and are shown greyed out.</summary>
public sealed class AlbumTrackInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public TimeSpan Duration { get; set; }
    public string? SongId { get; set; }
    public string? FilePath { get; set; }
    public string? ThumbnailPath { get; set; }
    public bool IsLocal { get; set; }
    public bool IsMissing { get; set; }
}

/// <summary>Album summary/metadata returned to search, home and the album view.</summary>
public sealed class AlbumInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public uint Year { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? ArtworkPath { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
    public DateTime? CachedUtc { get; set; }
    public bool IsComplete { get; set; }
    public int LocalTrackCount { get; set; }
    public int MissingTrackCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime DateAddedUtc { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<AlbumTrackInfo> Tracks { get; set; } = new();
}

/// <summary>Persisted provider result. Only provider metadata/tracks are cached; local matching stays live.</summary>
public sealed class AlbumCacheEntry
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public uint Year { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? ArtworkPath { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
    public DateTime FetchedUtc { get; set; }
    public List<AlbumTrackInfo> Tracks { get; set; } = new();
}

/// <summary>Provider album match, including its ordered track list when the provider exposes one.</summary>
public sealed class AlbumProviderResult
{
    public string Provider { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public uint Year { get; set; }
    public string Genre { get; set; } = string.Empty;
    public string? ArtworkUrl { get; set; }
    public byte[]? ArtworkBytes { get; set; }
    public string? ArtworkMimeType { get; set; }
    public List<AlbumTrackInfo> Tracks { get; set; } = new();
    public string? Error { get; set; }
}
