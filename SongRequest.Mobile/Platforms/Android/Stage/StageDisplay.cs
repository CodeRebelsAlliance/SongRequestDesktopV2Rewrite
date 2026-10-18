using System;
using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.Views;
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;

namespace SongRequest.Mobile;

/// <summary>
/// Shows the presentation on a second display (HDMI/USB-C), which is what a
/// projector or a TV plugged into a phone needs.
///
/// The display gets the same page the LAN/TV case serves, from the local stage
/// server, so there is one presentation implementation and one data source.
/// </summary>
internal static class StageDisplay
{
    private static StagePresentation? _presentation;

    /// <summary>Whether a secondary display is currently connected.</summary>
    public static bool IsSupported => SecondaryDisplay() != null;

    /// <summary>Shows the presentation on the secondary display, if there is one.</summary>
    public static bool Show(string url)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => Show(url));
            return true;
        }

        try
        {
            var display = SecondaryDisplay();
            if (display == null) return false;

            var activity = Platform.CurrentActivity as Activity;
            if (activity == null) return false;

            Hide();

            _presentation = new StagePresentation(activity, display, url);
            _presentation.Show();
            Android.Util.Log.Debug("SongRequest", $"presentation shown on display {display.DisplayId}");
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"presentation on a second display failed: {ex.Message}");
            return false;
        }
    }

    public static void Hide()
    {
        try { _presentation?.Dismiss(); } catch { }
        _presentation = null;
    }

    private static Display? SecondaryDisplay()
    {
        try
        {
            var context = Android.App.Application.Context;
            var manager = (DisplayManager?)context.GetSystemService(Context.DisplayService);
            if (manager == null) return null;

            // Display.DefaultDisplay is the default display's id in this binding.
            var defaultId = Display.DefaultDisplay;
            var displays = manager.GetDisplays() ?? Array.Empty<Display>();
            foreach (var display in displays)
            {
                if (display == null || display.DisplayId == defaultId) continue;
                return display;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"display enumeration failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>The presentation window: a full-screen web view of the stage page.</summary>
    private sealed class StagePresentation : Presentation
    {
        private readonly Android.Webkit.WebView _web;

        public StagePresentation(Context context, Display display, string url) : base(context, display)
        {
            _web = new Android.Webkit.WebView(context);
            _web.Settings.JavaScriptEnabled = true;
            _web.Settings.DomStorageEnabled = true;
            _web.SetBackgroundColor(Android.Graphics.Color.Black);

            SetContentView(_web);
            Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            _web.LoadUrl(url);
        }

        protected override void OnStop()
        {
            try { _web.LoadUrl("about:blank"); } catch { }
            base.OnStop();
        }
    }
}
