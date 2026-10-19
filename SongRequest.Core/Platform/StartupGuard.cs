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
    internal const string TrayDisabledFileName = "tray-disabled.marker";

    private const string SafeModeOkFileName = "safe-mode-ok.marker";

    /// <summary>How many unfinished starts in a row before safe mode kicks in.</summary>
    internal const int FailureThreshold = 2;

    /// <summary>
    /// How many clean starts in safe mode before the GPU path is tried again.
    /// Safe mode was previously permanent until <c>--gpu</c>: one bad day (a
    /// Wayland crash, or a start interrupted twice during an update) left the
    /// webview software-rendered — visibly less smooth — forever.
    /// </summary>
    internal const int SafeModeRecoveryStarts = 3;

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
    public static void Complete()
    {
        Delete(MarkerPath);

        // A start that reached a rendered UI proves the previous crash was not
        // caused by the tray: forgetting "a tray was used" means a later, unrelated
        // crash loop cannot leave the tray switched off for good.
        ClearTrayUsed();

        // Safe mode is sticky, but not forever: after a few clean starts in it, try
        // the hardware path again. If that crashes, the failure counter re-enters
        // safe mode as before.
        if (SafeModePersisted && Increment(SafeModeOkPath) >= SafeModeRecoveryStarts)
        {
            ClearSafeMode();
            Delete(SafeModeOkPath);
        }
    }

    /// <summary>True when this run recovered from repeated unfinished starts.</summary>
    public static bool CrashRecoveryThisRun { get; private set; }

    /// <summary>Remembers that this machine needs the conservative webview setup.</summary>
    public static void EnterSafeMode()
    {
        CrashRecoveryThisRun = true;
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppDataDir);
            File.WriteAllText(SafeModePath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch { }

        Delete(MarkerPath);
    }

    /// <summary>Forgets safe mode (used by <c>--gpu</c>).</summary>
    public static void ClearSafeMode()
    {
        Delete(SafeModePath);
        Delete(SafeModeOkPath);
    }

    private static string SafeModeOkPath => Path.Combine(AppPaths.LocalAppDataDir, SafeModeOkFileName);

    /// <summary>Increments a counter marker and returns the new value.</summary>
    private static int Increment(string path)
    {
        try
        {
            var current = 0;
            if (File.Exists(path))
                int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out current);

            var next = current + 1;
            Directory.CreateDirectory(AppPaths.LocalAppDataDir);
            File.WriteAllText(path, next.ToString(CultureInfo.InvariantCulture));
            return next;
        }
        catch
        {
            return int.MaxValue; // Never block recovery on bookkeeping.
        }
    }

    private static string TrayDisabledPath => Path.Combine(AppPaths.LocalAppDataDir, TrayDisabledFileName);

    /// <summary>
    /// True when the tray icon was switched off after a start crashed while creating
    /// it (GTK/AppIndicator failures are native and cannot be caught).
    /// </summary>
    public static bool TrayDisabled
    {
        get
        {
            try { return File.Exists(TrayDisabledPath); } catch { return false; }
        }
    }

    /// <summary>Remembers that the tray must stay off (cleared when the user re-enables it).</summary>
    public static void DisableTray()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppDataDir);
            File.WriteAllText(TrayDisabledPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }

    /// <summary>Lets the tray be tried again (used when the user changes the tray settings).</summary>
    public static void ClearTrayDisabled() => Delete(TrayDisabledPath);

    private static string TrayUsedPath => Path.Combine(AppPaths.LocalAppDataDir, "tray-used.marker");

    /// <summary>True when the previous start had created a tray icon.</summary>
    public static bool TrayUsedLastRun
    {
        get
        {
            try { return File.Exists(TrayUsedPath); } catch { return false; }
        }
    }

    /// <summary>Records that this start created a tray icon (so a crash can be blamed on it).</summary>
    public static void NoteTrayUsed()
    {
        try { Directory.CreateDirectory(AppPaths.LocalAppDataDir); File.WriteAllText(TrayUsedPath, "1"); } catch { }
    }

    /// <summary>Forgets that a tray icon was used.</summary>
    public static void ClearTrayUsed() => Delete(TrayUsedPath);

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
