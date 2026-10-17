using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Linux desktop integration: the application id, the window icon and the generated
/// <c>.desktop</c> entry.
///
/// Wayland has no per-window icon protocol — the compositor resolves the icon from
/// the <c>.desktop</c> file whose id matches the window's app id. That is why the
/// icon has to be *installed* (and the app id set before GTK starts) instead of
/// only being applied to the window; on X11 the GTK default icon is set as well.
/// </summary>
public static class LinuxDesktopIntegration
{
    internal const string AppId = "songrequest";
    internal const string IconFileName = "SRappIcon.png";

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Sets the GLib program name, which GTK uses as the Wayland app id. Must run
    /// before GTK is initialised (i.e. before the first window is created).
    /// </summary>
    public static void EnsureApplicationId()
    {
        if (!IsSupported) return;
        try { GSetPrgname(AppId); } catch { }
    }

    /// <summary>Sets the icon GTK applies to every window (X11; harmless on Wayland).</summary>
    public static void ApplyWindowIcon(string iconPath)
    {
        if (!IsSupported) return;
        try
        {
            if (!File.Exists(iconPath)) return;
            GtkSetDefaultIconFromFile(iconPath, out _);
        }
        catch
        {
            // The GTK version/function set differs; the .desktop entry still covers us.
        }
    }

    /// <summary>
    /// Installs/refreshes the user-level <c>.desktop</c> entry so the dock, task bar
    /// and app menu show the application icon (and the app can be launched from the
    /// menu). Best effort.
    /// </summary>
    public static void EnsureApplicationEntry(string launcherPath, string iconPath)
        => EnsureApplicationEntry(launcherPath, iconPath, dataHome: null);

    /// <summary>Installs the entry under an explicit XDG data home (used by tests).</summary>
    internal static void EnsureApplicationEntry(string launcherPath, string iconPath, string? dataHome)
    {
        if (!IsSupported) return;
        try
        {
            var path = ApplicationEntryPath(dataHome);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var content = BuildApplicationEntry(launcherPath, iconPath);
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content);
        }
        catch
        {
            // Desktop integration is a convenience; never let it break startup.
        }
    }

    // ── Pure builders (unit-tested) ────────────────────────────────────────

    internal static string ApplicationEntryPath(string? dataHome = null)
        => Path.Combine(DefaultDataHome(dataHome), "applications", AppId + ".desktop");

    internal static string BuildApplicationEntry(string launcherPath, string iconPath) => string.Join("\n", new[]
    {
        "[Desktop Entry]",
        "Type=Application",
        "Version=1.0",
        "Name=SongRequest Desktop",
        "Comment=Song request queue, soundboard and playback for live events.",
        $"Exec=\"{launcherPath}\"",
        $"Icon={iconPath}",
        // Compositors match a running window to this file through the app id / WM class.
        $"StartupWMClass={AppId}",
        "StartupNotify=true",
        "Terminal=false",
        "Categories=AudioVideo;Audio;Player;",
    }) + "\n";

    private static string DefaultDataHome(string? dataHome = null)
    {
        if (!string.IsNullOrEmpty(dataHome)) return dataHome!;
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg)) return xdg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    // ── native ─────────────────────────────────────────────────────────────

    [DllImport("libglib-2.0.so.0", CharSet = CharSet.Ansi, EntryPoint = "g_set_prgname")]
    private static extern void GSetPrgname(string prgname);

    [DllImport("libgtk-3.so.0", CharSet = CharSet.Ansi, EntryPoint = "gtk_window_set_default_icon_from_file")]
    private static extern void GtkSetDefaultIconFromFile(string filename, out IntPtr error);
}
