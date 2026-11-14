using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Detects the Linux distribution format the desktop app is running from.
///
/// A regular <c>.tar.gz</c> install keeps its data next to the executable, but an
/// AppImage is mounted read-only and a Flatpak is an immutable sandbox. Both need
/// their writable folders under the user's XDG data directory instead.
/// </summary>
public static class LinuxPackaging
{
    public const string FlatpakAppId = "net.bycra.SongRequest";

    /// <summary>True when the process was started from an AppImage.</summary>
    public static bool IsAppImage => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(AppImagePath);

    /// <summary>The original <c>.AppImage</c> path (not the temporary mount path).</summary>
    public static string? AppImagePath => ReadEnvironment("APPIMAGE");

    /// <summary>True when the process runs inside a Flatpak sandbox.</summary>
    public static bool IsFlatpak
        => OperatingSystem.IsLinux()
           && (!string.IsNullOrWhiteSpace(FlatpakId) || SafeFileExists("/.flatpak-info"));

    /// <summary>The Flatpak application id reported by the runtime.</summary>
    public static string? FlatpakId => ReadEnvironment("FLATPAK_ID");

    /// <summary>
    /// Moves downloads, the library, caches and config to a writable XDG data
    /// directory for AppImage/Flatpak installs. A normal tar.gz install keeps the
    /// historical layout next to the executable.
    /// </summary>
    public static void ConfigureAppPaths()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!IsAppImage && !IsFlatpak) return;

        var root = WritableDataRoot();
        if (string.IsNullOrWhiteSpace(root)) return;

        AppPaths.UseDataRoot(root);
        CopyBundledChime();
    }

    /// <summary>
    /// <c>$XDG_DATA_HOME/SongRequestDesktopV2Rewrite</c>, falling back to
    /// <c>~/.local/share/SongRequestDesktopV2Rewrite</c>.
    /// </summary>
    internal static string WritableDataRoot()
    {
        var dataHome = ReadEnvironment("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dataHome = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(dataHome, "SongRequestDesktopV2Rewrite");
    }

    private static void CopyBundledChime()
    {
        try
        {
            var source = Path.Combine(AppPaths.BaseDirectory, "announcement.mp3");
            var target = AppPaths.ChimePath;
            if (File.Exists(source) && !File.Exists(target))
                File.Copy(source, target);
        }
        catch
        {
            // The announcement chime is optional; a read-only source must not break startup.
        }
    }

    private static string? ReadEnvironment(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}
