using System;
using Microsoft.Maui.ApplicationModel;

namespace SongRequest.Mobile;

/// <summary>
/// Keeps the phone screen awake while the mobile lyrics view is open. The
/// presentation/HDMI window manages its own keep-screen-on flag separately.
/// </summary>
public static class ScreenAwake
{
    private static volatile bool _lyricsRequested;
    private static volatile bool _suspended;

    /// <summary>Called by the mobile music page when the lyrics view opens/closes.</summary>
    public static void SetLyrics(bool enabled)
    {
        _lyricsRequested = enabled;
        Apply();
    }

    /// <summary>The app went to the background: release the wake lock.</summary>
    public static void Suspend()
    {
        _suspended = true;
        Apply();
    }

    /// <summary>The app came back: restore the lyrics wake lock if still requested.</summary>
    public static void Resume()
    {
        _suspended = false;
        Apply();
    }

    private static void Apply()
    {
        bool keep = _lyricsRequested && !_suspended;
        MainThread.BeginInvokeOnMainThread(() => ApplyCore(keep));
    }

    private static void ApplyCore(bool keep)
    {
#if ANDROID
        try
        {
            var activity = Platform.CurrentActivity as Android.App.Activity;
            var window = activity?.Window;
            if (window == null) return;

            if (keep) window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
            else window.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        }
        catch { }
#elif IOS
        try { UIKit.UIApplication.SharedApplication.IdleTimerDisabled = keep; } catch { }
#endif
    }
}
