using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The slice of the online metadata layer the artist catalogue needs. Like the
/// album provider seam it keeps grouping, caching and matching testable without
/// network calls.
/// </summary>
public interface IArtistMetadataProvider
{
    Task<ArtistProviderResult> FindArtistAsync(
        string artist,
        CancellationToken ct = default);

    Task<ArtworkResult> FetchArtworkAsync(string url, CancellationToken ct = default);
}

/// <summary>A local track on an artist page.</summary>
public sealed class ArtistTrackInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public uint Year { get; set; }
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public TimeSpan Duration { get; set; }
    public string? SongId { get; set; }
    public string? FilePath { get; set; }
    public string? ThumbnailPath { get; set; }
    public int PlayCount { get; set; }
    public bool IsMissing { get; set; }
}

/// <summary>An album shown on an artist page. Provider albums that are not in the library are not local.</summary>
public sealed class ArtistAlbumInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public uint Year { get; set; }
    public string? ArtworkPath { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
    public bool IsLocal { get; set; }
    public int LocalTrackCount { get; set; }
    public int MissingTrackCount { get; set; }
    public bool IsComplete { get; set; } = true;
}

/// <summary>Artist summary/metadata returned to search, home and the artist view.</summary>
public sealed class ArtistInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? ArtworkPath { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
    public DateTime? CachedUtc { get; set; }
    public int LocalTrackCount { get; set; }
    public int AlbumCount { get; set; }
    public int MissingAlbumCount { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime DateAddedUtc { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<ArtistTrackInfo> Tracks { get; set; } = new();
    /// <summary>Local albums only (shared with the album catalogue).</summary>
    public List<AlbumInfo> Albums { get; set; } = new();
    /// <summary>Local albums plus provider albums that are not in the library.</summary>
    public List<ArtistAlbumInfo> CatalogAlbums { get; set; } = new();
}

/// <summary>
/// Persisted provider metadata. Local tracks and albums are matched live so the
/// artist page always reflects the current library.
/// </summary>
public sealed class ArtistCacheEntry
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? ArtworkPath { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
    public DateTime FetchedUtc { get; set; }
    public List<ArtistProviderAlbum> Albums { get; set; } = new();
}

/// <summary>Provider discography entry, matched against local albums live.</summary>
public sealed class ArtistProviderAlbum
{
    public string Title { get; set; } = string.Empty;
    public uint Year { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? Provider { get; set; }
}

/// <summary>Provider artist match, including optional artwork bytes.</summary>
public sealed class ArtistProviderResult
{
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? ArtworkUrl { get; set; }
    public List<ArtistProviderAlbum> Albums { get; set; } = new();
    public string? Error { get; set; }
}
