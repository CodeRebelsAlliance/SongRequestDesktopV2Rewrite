namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Command-line launch options. Autostart entries pass <c>--minimized</c> so the
/// app can start hidden/in the tray.
/// </summary>
public static class AppLaunchArgs
{
    public const string MinimizedFlag = "--minimized";

    /// <summary>True when the app was launched with <c>--minimized</c>.</summary>
    public static bool StartMinimized { get; private set; }

    public static void Parse(string[]? args)
    {
        StartMinimized = args != null && System.Array.Exists(args, a =>
            string.Equals(a, MinimizedFlag, System.StringComparison.OrdinalIgnoreCase));
    }
}
