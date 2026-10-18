using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Where a downloaded song lives, and which audio container to download.
///
/// The desktop has always saved YouTube's audio-only stream as <c>&lt;id&gt;.mp3</c>
/// whatever the container actually was (FFmpeg decodes by content). Mobile prefers
/// the AAC-in-MP4 stream (<c>.m4a</c>), because the platform decoders handle it
/// natively and iOS has no Opus/WebM support at all.
///
/// Existing downloads keep working either way: a song's file is looked up under
/// every extension we have ever used, so a library built before this change is
/// untouched.
/// </summary>
internal static class DownloadedAudio
{
    /// <summary>Extensions a downloaded song may have, in lookup order.</summary>
    private static readonly string[] KnownExtensions = { ".mp3", ".m4a", ".webm", ".opus", ".aac", ".ogg" };

    /// <summary>
    /// The container to prefer when picking an audio stream, or null for the
    /// historical "highest bitrate" choice. <c>mp4</c> selects the AAC stream that
    /// both mobile platforms can decode.
    /// </summary>
    public static string? PreferredContainer { get; set; }

    /// <summary>Extension new downloads get (derived from the preferred container).</summary>
    public static string PreferredExtension =>
        string.IsNullOrEmpty(PreferredContainer) ? ".mp3"
        : PreferredContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? ".m4a"
        : "." + PreferredContainer.TrimStart('.').ToLowerInvariant();

    /// <summary>Whether a stream's container matches the preference.</summary>
    public static bool MatchesPreferredContainer(string? container)
    {
        if (string.IsNullOrEmpty(PreferredContainer) || string.IsNullOrEmpty(container)) return false;

        // "mp4" also covers the "m4a" spelling YoutubeExplode uses for AAC audio.
        if (PreferredContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            return container.Equals("mp4", StringComparison.OrdinalIgnoreCase)
                || container.Equals("m4a", StringComparison.OrdinalIgnoreCase);

        return container.Equals(PreferredContainer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An already-downloaded file for this video, or null.</summary>
    public static string? Existing(string videoId, string? folder = null)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        var directory = folder ?? AppPaths.DownloadPath;
        foreach (var extension in KnownExtensions)
        {
            var candidate = Path.Combine(directory, videoId + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Where a download of this video should go.</summary>
    public static string PathFor(string videoId, string? folder = null)
    {
        var directory = folder ?? AppPaths.DownloadPath;

        // Never re-download over an existing file, and never lose one either.
        var existing = Existing(videoId, directory);
        if (existing != null) return existing;

        return Path.Combine(directory, videoId + PreferredExtension);
    }
}
