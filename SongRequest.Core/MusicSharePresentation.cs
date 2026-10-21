using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Builds the presentation payloads for a received Music Share. The desktop
/// presentation and the mobile HDMI/LAN stage both use these shapes, so the two
/// screens cannot drift apart.
/// </summary>
public static class MusicSharePresentation
{
    public const string Provider = "MusicShare";

    /// <summary>
    /// Wraps a received share thumbnail in a data URL. The payload can be PNG
    /// (embedded cover art) even though the protocol calls it JPEG, so the MIME
    /// type is derived from the base64 signature instead of assuming JPEG.
    /// </summary>
    public static string? ThumbnailDataUrl(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        // Signatures at the start of the base64 for JPEG, PNG, GIF and WebP.
        var mime = base64.StartsWith("iVBOR", StringComparison.Ordinal) ? "image/png"
            : base64.StartsWith("R0lGOD", StringComparison.Ordinal) ? "image/gif"
            : base64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp"
            : "image/jpeg";

        return $"data:{mime};base64,{base64}";
    }

    /// <summary>Now-playing fields read by <c>presentation.js</c>.</summary>
    public static Dictionary<string, object?> BuildNowPlaying(MusicShareMetadata metadata, bool isPlaying = true)
        => new()
        {
            ["title"] = metadata.Title ?? string.Empty,
            ["artist"] = metadata.Artist ?? string.Empty,
            ["duration"] = TimeDisplay.Clock(TimeSpan.FromSeconds(Math.Max(0, metadata.TotalSeconds))),
            ["currentTime"] = metadata.ElapsedSeconds,
            ["totalTime"] = metadata.TotalSeconds,
            ["thumbnail"] = ThumbnailDataUrl(metadata.ThumbnailData),
            ["isPlaying"] = isPlaying,
        };

    /// <summary>Lyrics fields read by <c>presentation.js</c>.</summary>
    public static Dictionary<string, object> BuildLyrics(MusicShareMetadata metadata)
    {
        var lyrics = metadata.Lyrics ?? string.Empty;
        var data = new Dictionary<string, object> { ["provider"] = Provider };
        var synced = new List<object>();

        if (metadata.HasSyncedLyrics && !string.IsNullOrWhiteSpace(lyrics))
        {
            var parsed = new LyricsResult { Found = true, SyncedLyrics = lyrics };
            synced = parsed.ParseSyncedLines()
                .Select(line => (object)new { time = line.Time.TotalSeconds, text = line.Text })
                .ToList();
        }

        if (synced.Count > 0)
        {
            data["syncedLyrics"] = synced;
            data["hasSynced"] = true;
        }
        else
        {
            // No (parseable) lyrics: send an empty list so the presentation clears
            // the previous song instead of keeping stale lines.
            data["plainLyrics"] = SplitLyricLines(lyrics);
            data["hasSynced"] = false;
        }

        return data;
    }

    /// <summary>An empty now-playing object, used when the selected source has no track.</summary>
    public static Dictionary<string, object?> EmptyNowPlaying()
        => new()
        {
            ["title"] = string.Empty,
            ["artist"] = string.Empty,
            ["duration"] = "00:00",
            ["currentTime"] = 0d,
            ["totalTime"] = 0d,
            ["thumbnail"] = null,
            ["isPlaying"] = false,
        };

    private static List<string> SplitLyricLines(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return new List<string>();
        return lyrics
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }
}
