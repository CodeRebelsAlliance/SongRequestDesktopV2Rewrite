using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The editable metadata of one library song, independent of whether it lives in
/// the audio file's tags or in library.json.
/// </summary>
public sealed class SongMetadataSnapshot
{
    public string SongId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool FileExists { get; set; }
    public bool CanWriteToFile { get; set; }
    public string FileFormat { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public uint Year { get; set; }
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public uint Bpm { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public string Lyricist { get; set; } = string.Empty;
    public string Conductor { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Isrc { get; set; } = string.Empty;
    public string Grouping { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    /// <summary>Plain text or LRC-formatted lyrics.</summary>
    public string Lyrics { get; set; } = string.Empty;

    /// <summary>True when the timed lyrics are in LRC format.</summary>
    public bool LyricsAreSynced { get; set; }

    /// <summary>Artwork bytes (null when the song has no artwork).</summary>
    public byte[]? ArtworkBytes { get; set; }
    public string? ArtworkMimeType { get; set; }

    public bool MetadataStoredInLibrary { get; set; }
    public bool LyricsStoredInLibrary { get; set; }
    public bool ArtworkStoredInLibrary { get; set; }

    /// <summary>A non-fatal problem while reading/writing the file.</summary>
    public string? Warning { get; set; }

    public MetadataProviderSettings Providers { get; set; } = new();
}

/// <summary>Values submitted by the metadata editor.</summary>
public sealed class SongMetadataEdit
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public uint Year { get; set; }
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public uint Bpm { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public string Lyricist { get; set; } = string.Empty;
    public string Conductor { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string Isrc { get; set; } = string.Empty;
    public string Grouping { get; set; } = string.Empty;
    public string Lyrics { get; set; } = string.Empty;
}

public enum MetadataWriteTarget
{
    /// <summary>Tags were written into the audio file.</summary>
    File,

    /// <summary>The file could not be written; library.json is authoritative.</summary>
    Library
}

public sealed class SongMetadataSaveResult
{
    public bool Success { get; set; }
    public MetadataWriteTarget Target { get; set; }
    public string? Warning { get; set; }
    public SongMetadataSnapshot? Metadata { get; set; }
}

/// <summary>Which online catalogues may be contacted for metadata and artwork.</summary>
public sealed class MetadataProviderSettings
{
    public bool MusicBrainz { get; set; } = true;
    public bool ITunes { get; set; } = true;
    public bool Deezer { get; set; } = true;
    public bool CoverArtArchive { get; set; } = true;
    public bool AcoustId { get; set; }
    public string AcoustIdApiKey { get; set; } = string.Empty;
    public bool Discogs { get; set; }
    public string DiscogsToken { get; set; } = string.Empty;

    /// <summary>Provider names in lookup priority order (highest first).</summary>
    public List<string> Priority { get; set; } = new()
    {
        "MusicBrainz", "iTunes", "Deezer", "CoverArtArchive", "AcoustID", "Discogs"
    };

    public bool AnyEnabled => MusicBrainz || ITunes || Deezer || CoverArtArchive || AcoustId || Discogs;

    public static MetadataProviderSettings FromConfig(Config? cfg) => new()
    {
        MusicBrainz = cfg?.MetadataProviderMusicBrainz ?? true,
        ITunes = cfg?.MetadataProviderITunes ?? true,
        Deezer = cfg?.MetadataProviderDeezer ?? true,
        CoverArtArchive = cfg?.MetadataProviderCoverArtArchive ?? true,
        AcoustId = cfg?.MetadataProviderAcoustId ?? false,
        AcoustIdApiKey = cfg?.AcoustIdApiKey ?? string.Empty,
        Discogs = cfg?.MetadataProviderDiscogs ?? false,
        DiscogsToken = cfg?.DiscogsToken ?? string.Empty,
        Priority = NormalizePriority(cfg?.MetadataProviderPriority),
    };

    internal static List<string> NormalizePriority(IEnumerable<string>? priority)
    {
        var defaults = new List<string> { "MusicBrainz", "iTunes", "Deezer", "CoverArtArchive", "AcoustID", "Discogs" };
        var known = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in priority ?? Enumerable.Empty<string>())
        {
            var match = defaults.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !result.Contains(match, StringComparer.OrdinalIgnoreCase))
                result.Add(match);
        }
        foreach (var name in defaults)
        {
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }
        return result;
    }
}

/// <summary>A match returned by an online catalogue.</summary>
public sealed class MetadataCandidate
{
    public string Provider { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public uint Year { get; set; }
    public uint Track { get; set; }
    public uint Disc { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ArtworkUrl { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>0..1 confidence after title/artist/duration matching.</summary>
    public double Score { get; set; }

    public string? Error { get; set; }
}

/// <summary>Result of a music identification/search request.</summary>
public sealed class MetadataSearchResult
{
    public List<MetadataCandidate> Candidates { get; set; } = new();
    public MetadataCandidate? Best { get; set; }
    public string? Error { get; set; }
    public string Query { get; set; } = string.Empty;
}

/// <summary>Downloaded artwork from a provider or the user's file picker.</summary>
public sealed class ArtworkResult
{
    public byte[]? Bytes { get; set; }
    public string? MimeType { get; set; }
    public string? Error { get; set; }
}
