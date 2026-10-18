using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Command-line launch options. Autostart entries pass <c>--minimized</c> so the
/// app can start hidden/in the tray.
///
/// The Linux graphics flags exist because GTK/WebKitGTK can fail on some
/// driver/compositor combinations; they are applied by
/// <see cref="LinuxWebViewEnvironment"/> and documented in the README.
/// </summary>
public static class AppLaunchArgs
{
    public const string MinimizedFlag = "--minimized";

    /// <summary>Force the conservative webview setup (no DMA-BUF/compositing, X11).</summary>
    public const string SafeModeFlag = "--safe-mode";

    /// <summary>Keep the hardware-accelerated (DMA-BUF) webview path.</summary>
    public const string GpuFlag = "--gpu";

    /// <summary>Force GTK to use the X11 backend instead of Wayland.</summary>
    public const string X11Flag = "--x11";

    /// <summary>Force GTK to use the Wayland backend.</summary>
    public const string WaylandFlag = "--wayland";

    /// <summary>True when the app was launched with <c>--minimized</c>.</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>True when the app was launched with <c>--safe-mode</c>.</summary>
    public static bool SafeMode { get; private set; }

    /// <summary>True when the app was launched with <c>--gpu</c>.</summary>
    public static bool PreferGpu { get; private set; }

    /// <summary>True when the app was launched with <c>--x11</c>.</summary>
    public static bool ForceX11 { get; private set; }

    /// <summary>True when the app was launched with <c>--wayland</c>.</summary>
    public static bool ForceWayland { get; private set; }

    public static void Parse(string[]? args)
    {
        StartMinimized = Has(args, MinimizedFlag);
        SafeMode = Has(args, SafeModeFlag);
        PreferGpu = Has(args, GpuFlag);
        ForceX11 = Has(args, X11Flag);
        ForceWayland = Has(args, WaylandFlag);
    }

    private static bool Has(string[]? args, string flag)
        => args != null && Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
