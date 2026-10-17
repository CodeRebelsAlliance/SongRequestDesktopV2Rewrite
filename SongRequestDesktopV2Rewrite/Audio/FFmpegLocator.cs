using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Locates the FFmpeg / FFprobe executables (bundled <c>tools</c>, per-user
/// <c>tools</c>, app directory, or PATH). The result is cached.
/// </summary>
public static class FFmpegLocator
{
    public static string? FindFfmpeg() => ToolLocator.Find("ffmpeg");

    public static string? FindFfprobe() => ToolLocator.Find("ffprobe");

    /// <summary>Clears the cached lookups (called after an auto-download).</summary>
    public static void Invalidate() => ToolLocator.Invalidate();
}
