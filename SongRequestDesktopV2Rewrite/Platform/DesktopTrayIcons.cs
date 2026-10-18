using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Desktop tray implementation: <c>Shell_NotifyIcon</c> on Windows and
/// AppIndicator/StatusNotifierItem on Linux (when
/// <c>libayatana-appindicator3</c>/<c>libappindicator3</c> is present).
/// macOS has no tray in this app, so the options stay disabled there.
/// </summary>
internal static class DesktopTrayIcons
{
    public static bool IsSupported =>
        OperatingSystem.IsWindows() ||
        (OperatingSystem.IsLinux() && AppIndicatorNative.IsAvailable);

    public static ITrayIcon Create(string tooltip, string? iconPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new Win32TrayIcon(tooltip); }
            catch { return new NullTrayIcon(); }
        }

        if (OperatingSystem.IsLinux())
        {
            // Requires GTK to be initialised (the Photino window does that) and a
            // StatusNotifier host; any failure falls back to no tray.
            try { return new LinuxTrayIcon(tooltip, iconPath); }
            catch { return new NullTrayIcon(); }
        }

        return new NullTrayIcon();
    }
}
