using System;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// The core's <see cref="IUiShell"/> on mobile: the desktop's sub-windows become
/// tabs and full-screen pages, and page messages are routed to the HybridWebView
/// that owns them.
/// </summary>
public sealed class MobileShell : IUiShell
{
    private readonly MainPage _page;

    public MobileShell(MainPage page)
    {
        _page = page;

        // The interop invokes these to push state into a "window". On mobile they
        // are dropped when the page is not resident; every page re-requests its
        // state when it loads, so nothing is lost permanently.
        MusicPlayerSendMessage = json => _page.SendToMusic(json);
        PresentationSendMessage = json => _page.SendTo("presentation.html", json);
        SoundboardSendMessage = json =>
        {
            if (!_page.SendTo(MainPage.MobileSoundboardFile, json))
                _page.SendTo("soundboard.html", json);
        };
        VisualizationSendMessage = json => _page.SendTo("visualization.html", json);
        AnnouncementSendMessage = json => _page.SendTo("announcement.html", json);
    }

    // ── Player ───────────────────────────────────────────────────────────────
    public void ShowMusicPlayerWindow()
    {
        // In music mode the startup flow applies the mode (landing on Home) and
        // then asks for the player window; that request must not skip Home.
        if (_page.ConsumeInitialPlayerOpen()) return;
        _page.Select("player");
    }

    public void CloseMusicPlayerWindow()
    {
        // Tabs cannot be closed on mobile; leaving the tab is the user's choice.
    }

    public Action<string>? MusicPlayerSendMessage { get; set; }

    // ── Presentation ─────────────────────────────────────────────────────────
    public void ShowPresentationWindow()
    {
        // The stage tab is the mobile console: link/QR, zoom, source and a live
        // preview. Starting it publishes the presentation to TV/HDMI.
        _page.ShowFileInTab(MainPage.MobileStageFile, "stage");

#if ANDROID
        // Serve the page to a TV browser and mirror it onto an HDMI display when
        // one is connected.
        StageMirror.Publish();
#endif
    }

    public void ClosePresentationWindow() => _page.SetImmersive(false);

    public Action<string>? PresentationSendMessage { get; set; }

    // ── Soundboard ───────────────────────────────────────────────────────────
    public void ShowSoundboardWindow() => _page.Select("board");

    public void CloseSoundboardWindow() { }

    public Action<string>? SoundboardSendMessage { get; set; }

    // ── Visualization ────────────────────────────────────────────────────────
    public void ShowVisualizationWindow() => _page.ShowFileInTab("visualization.html", "stage");

    public void CloseVisualizationWindow() => _page.SetImmersive(false);

    public Action<string>? VisualizationSendMessage { get; set; }

    // ── Announcement ─────────────────────────────────────────────────────────
    // The announcement page is a control panel (press/release), so it shares the
    // stage tab with the presentation/visualization pages.
    public void ShowAnnouncementWindow() => _page.ShowFileInTab("announcement.html", "stage");

    public void CloseAnnouncementWindow() => _page.SetImmersive(false);

    public Action<string>? AnnouncementSendMessage { get; set; }

    /// <summary>
    /// Fullscreen on mobile = the stage page hides the native tab bar. Only the
    /// page that is actually showing can be made fullscreen, so the request is
    /// refused (and the page keeps its own icon honest) otherwise.
    /// </summary>
    public bool SetSubWindowFullscreen(string? windowName, bool enabled)
    {
        var file = FileFor(windowName);
        if (file == null || !string.Equals(_page.CurrentFile, file, StringComparison.Ordinal))
            return false;

        _page.SetImmersive(enabled);
        return true;
    }

    /// <summary>
    /// Rebuilds the mobile navigation for the operating mode chosen at startup.
    /// The startup flow calls this once the user finished launch/auth, so that is
    /// also when the native navigation bar appears.
    /// </summary>
    public void SetOperatingMode(StartupMode mode)
    {
        _page.SetNavigationVisible(true);
        _page.ConfigureMode(mode);
    }

    /// <summary>Quits the app (the web UI's Quit buttons).</summary>
    public void Close() => Application.Current?.Quit();

    /// <summary>Marshals background callbacks onto MAUI's UI thread.</summary>
    public void PostToUi(Action action) => Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(action);

    private static string? FileFor(string? windowName) => windowName switch
    {
        "visualization" => "visualization.html",
        "presentation" => "presentation.html",
        "musicplayer" => "musicplayer.html",
        "soundboard" => MainPage.MobileSoundboardFile,
        "requests" => MainPage.MobileRequestsFile,
        "announcement" => "announcement.html",
        _ => null,
    };
}
