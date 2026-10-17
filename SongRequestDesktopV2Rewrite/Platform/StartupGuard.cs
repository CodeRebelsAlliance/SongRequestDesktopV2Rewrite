using System;
using System.Globalization;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Detects a startup that never finished, so the next launch can fall back to a
/// more conservative webview configuration.
///
/// WebKitGTK/GTK failures (the Wayland <c>Error 71</c> crash, a blank webview) are
/// native problems the app cannot catch, so a marker file is written before the
/// window is created and removed once the webview has actually delivered a message
/// — i.e. once we know rendering works.
///
/// The marker counts how many starts in a row did not finish. A **single**
/// interrupted start (the user closes the app while it is still loading, a terminal
/// Ctrl+C during development) is ignored; only a repeat counts as a crash loop and
/// switches on safe mode, which is then remembered across launches so a user whose
/// machine needs it does not get a crash on every other start. <c>--gpu</c> clears
/// it again.
/// </summary>
public static class StartupGuard
{
    internal const string MarkerFileName = "startup-in-progress.marker";
    internal const string SafeModeFileName = "safe-mode.marker";

    /// <summary>How many unfinished starts in a row before safe mode kicks in.</summary>
    internal const int FailureThreshold = 2;

    internal static string MarkerPath => Path.Combine(AppPaths.LocalAppDataDir, MarkerFileName);

    internal static string SafeModePath => Path.Combine(AppPaths.LocalAppDataDir, SafeModeFileName);

    /// <summary>Number of consecutive starts that did not finish.</summary>
    public static int UnfinishedStarts
    {
        get
        {
            try
            {
                if (!File.Exists(MarkerPath)) return 0;
                var text = File.ReadAllText(MarkerPath).Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>True when the last starts repeatedly failed to finish.</summary>
    public static bool RepeatedStartupFailure => UnfinishedStarts >= FailureThreshold;

    /// <summary>True when safe mode was remembered from an earlier crashed start.</summary>
    public static bool SafeModePersisted
    {
        get
        {
            try { return File.Exists(SafeModePath); } catch { return false; }
        }
    }

    /// <summary>Counts a startup as in progress. Call before creating the window.</summary>
    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppDataDir);
            var next = UnfinishedStarts + 1;
            File.WriteAllText(MarkerPath, next.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Never let bookkeeping break startup.
        }
    }

    /// <summary>Clears the marker once the webview is confirmed to be rendering.</summary>
    public static void Complete() => Delete(MarkerPath);

    /// <summary>Remembers that this machine needs the conservative webview setup.</summary>
    public static void EnterSafeMode()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppDataDir);
            File.WriteAllText(SafeModePath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch { }

        Delete(MarkerPath);
    }

    /// <summary>Forgets safe mode (used by <c>--gpu</c>).</summary>
    public static void ClearSafeMode() => Delete(SafeModePath);

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
