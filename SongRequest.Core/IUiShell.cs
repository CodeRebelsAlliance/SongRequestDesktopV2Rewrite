using System;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The host's windows/screens, as the interop sees them.
///
/// On the desktop this is <c>NewUiWindow</c> (one Photino window per view); on
/// mobile a single shell maps each method onto a tab, a sheet or a full-screen
/// page. The interop only ever talks to this interface, so it stays free of
/// windowing technology.
/// </summary>
public interface IUiShell
{
    // ── Music player ─────────────────────────────────────────────────────────
    void ShowMusicPlayerWindow();
    void CloseMusicPlayerWindow();

    /// <summary>Sends a JSON message to the music player page (null until it exists).</summary>
    Action<string>? MusicPlayerSendMessage { get; set; }

    // ── Presentation ─────────────────────────────────────────────────────────
    void ShowPresentationWindow();
    void ClosePresentationWindow();
    Action<string>? PresentationSendMessage { get; set; }

    // ── Soundboard ───────────────────────────────────────────────────────────
    void ShowSoundboardWindow();
    void CloseSoundboardWindow();
    Action<string>? SoundboardSendMessage { get; set; }

    // ── Visualization ────────────────────────────────────────────────────────
    void ShowVisualizationWindow();
    void CloseVisualizationWindow();
    Action<string>? VisualizationSendMessage { get; set; }

    // ── Announcement ─────────────────────────────────────────────────────────
    void ShowAnnouncementWindow();
    void CloseAnnouncementWindow();
    Action<string>? AnnouncementSendMessage { get; set; }

    /// <summary>
    /// Applies native fullscreen to a sub-window. <paramref name="windowName"/> is
    /// the view name used by the web UI ("musicplayer", "visualization", …).
    /// Returns whether the request was handled by the host.
    /// </summary>
    bool SetSubWindowFullscreen(string? windowName, bool enabled);

    /// <summary>
    /// Applies the operating mode chosen at startup (song requests, music player,
    /// music share, soundboard). Mobile shells rebuild their navigation for it; the
    /// desktop has fixed windows, so the default implementation does nothing.
    /// </summary>
    void SetOperatingMode(StartupMode mode) { }

    /// <summary>Closes the whole application (the web UI's Quit buttons).</summary>
    void Close();
}
