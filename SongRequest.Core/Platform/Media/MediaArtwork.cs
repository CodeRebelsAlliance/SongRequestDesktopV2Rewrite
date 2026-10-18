using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Resolves a local image file for the currently playing song so the OS media
/// controls can show artwork (<c>mpris:artUrl</c>, SMTC thumbnail, ...).
///
/// Resolution order:
/// 1. the song's cached thumbnail data-URL (written to the artwork folder);
/// 2. the YouTube thumbnail cache (<c>thumbnail-cache/&lt;videoId&gt;.png|jpg</c>);
/// 3. embedded album art (via TagLib).
/// </summary>
public static class MediaArtwork
{
    /// <summary>Returns a local image path for <paramref name="song"/>, or null when none is available.</summary>
    public static string? Resolve(Song? song, string artworkDirectory)
    {
        if (song == null) return null;

        var key = StableKey(song);

        // 1. Cached thumbnail on the Song object (data URL).
        if (song.thumbnail is string dataUrl && !string.IsNullOrWhiteSpace(dataUrl))
        {
            var written = WriteDataUrl(dataUrl, artworkDirectory, key);
            if (written != null) return written;
        }

        // 2. YouTube thumbnail cache keyed by video id (the file name of the audio).
        var videoId = GetVideoId(song.songPath);
        if (!string.IsNullOrEmpty(videoId))
        {
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                var candidate = Path.Combine(AppPaths.ThumbnailCachePath, videoId + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3. Embedded album art.
        return TryExtractEmbeddedArtwork(song.songPath, artworkDirectory, key);
    }

    /// <summary>Decodes a <c>data:image/...;base64,...</c> (or bare base64) string to a local file.</summary>
    public static string? WriteDataUrl(string? dataUrl, string outputDirectory, string baseName)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;

        var idx = dataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        string payload;
        string extension;
        if (idx >= 0)
        {
            var header = dataUrl[..idx];
            payload = dataUrl[(idx + 8)..];
            extension = header.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : header.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
                : ".jpg";
        }
        else
        {
            payload = dataUrl.Trim();
            extension = ".jpg";
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length == 0) return null;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, baseName + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the first embedded picture of an audio file to disk.</summary>
    public static string? TryExtractEmbeddedArtwork(string? filePath, string outputDirectory, string baseName)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        try
        {
            using var file = global::TagLib.File.Create(filePath);
            var picture = file.Tag?.Pictures?.FirstOrDefault();
            var bytes = picture?.Data?.Data;
            if (bytes is not { Length: > 0 }) return null;

            var mime = picture!.MimeType ?? "";
            var extension = mime.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : mime.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
                : ".jpg";

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, baseName + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The video id used as the download file name, or null for local files.</summary>
    public static string? GetVideoId(string? songPath)
    {
        if (string.IsNullOrEmpty(songPath)) return null;
        try
        {
            return Path.GetFileNameWithoutExtension(songPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A deterministic, file-system-safe key for a song.</summary>
    public static string StableKey(Song song)
    {
        var seed = !string.IsNullOrEmpty(song.songPath) ? song.songPath
            : (song.Title ?? "") + "\u0000" + (song.Artist ?? "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
