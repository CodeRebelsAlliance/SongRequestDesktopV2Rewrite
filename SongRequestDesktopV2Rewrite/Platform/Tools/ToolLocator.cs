using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Locates external tool executables (ffmpeg, ffprobe, yt-dlp). Looked up in a
/// bundled <c>tools</c> folder next to the app, the per-user <c>tools</c> folder,
/// the app directory, then the PATH. Results are cached per executable name.
/// </summary>
internal static class ToolLocator
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string baseName)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(baseName, out var cached)) return cached;
            var resolved = Resolve(baseName);
            Cache[baseName] = resolved;
            return resolved;
        }
    }

    /// <summary>Clears the cache (e.g. after downloading a tool).</summary>
    public static void Invalidate()
    {
        lock (Sync) Cache.Clear();
    }

    private static string? Resolve(string baseName)
    {
        var exe = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

        foreach (var directory in new[]
        {
            AppPaths.ToolsDirectory,
            AppPaths.UserToolsDirectory,
            AppPaths.BaseDirectory,
        })
        {
            try
            {
                var candidate = Path.Combine(directory, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return ExistsOnPath(exe) ? exe : null;
    }

    private static bool ExistsOnPath(string exe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
