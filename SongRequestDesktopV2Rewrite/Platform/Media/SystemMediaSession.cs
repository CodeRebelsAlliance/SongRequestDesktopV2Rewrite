using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Playback state reported to the operating system's media controls.</summary>
public enum MediaPlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// The "now playing" information pushed to the operating system (MPRIS on Linux,
/// SMTC on Windows, MPNowPlayingInfoCenter on macOS). Artwork is a local file path
/// so each backend can turn it into the URI form it needs.
/// </summary>
public sealed record MediaMetadata(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string? ArtworkPath);

/// <summary>
/// A cross-platform system media session: the OS can display the current track
/// (title / artist / album / artwork / state / position) and send transport
/// commands back to the app.
///
/// Backends: <see cref="MprisMediaSession"/> (Linux) today; SMTC / MPNowPlaying
/// are added as further backends behind this same interface.
/// </summary>
public interface ISystemMediaSession : IDisposable
{
    /// <summary>Whether this backend is actually connected to the OS.</summary>
    bool IsSupported { get; }

    /// <summary>Human-readable backend name for the settings UI (e.g. "MPRIS").</summary>
    string BackendName { get; }

    /// <summary>Publishes the current track metadata (null clears it).</summary>
    void UpdateMetadata(MediaMetadata? metadata);

    /// <summary>Publishes the playback state and current position.</summary>
    void UpdatePlayback(MediaPlaybackState state, TimeSpan position);

    event EventHandler? PlayRequested;
    event EventHandler? PauseRequested;
    event EventHandler? TogglePlayPauseRequested;
    event EventHandler? NextRequested;
    event EventHandler? PreviousRequested;
    event EventHandler? StopRequested;

    /// <summary>Relative seek request (MPRIS <c>Seek</c>).</summary>
    event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Absolute position request (MPRIS <c>SetPosition</c>).</summary>
    event EventHandler<TimeSpan>? PositionSetRequested;

    /// <summary>Volume change requested by the OS mixer (0..1).</summary>
    event EventHandler<double>? VolumeRequested;

    /// <summary>Asked to surface the app window (MPRIS <c>Raise</c>).</summary>
    event EventHandler? RaiseRequested;

    /// <summary>Asked to quit the app (MPRIS <c>Quit</c>).</summary>
    event EventHandler? QuitRequested;
}

/// <summary>No-op session used when the platform has no supported backend.</summary>
public sealed class NullSystemMediaSession : ISystemMediaSession
{
    public bool IsSupported => false;

    public string BackendName => "none";

    public void UpdateMetadata(MediaMetadata? metadata) { }

    public void UpdatePlayback(MediaPlaybackState state, TimeSpan position) { }

#pragma warning disable CS0067 // Never raised: the null backend receives no OS commands.
    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? TogglePlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? PositionSetRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler? RaiseRequested;
    public event EventHandler? QuitRequested;
#pragma warning restore CS0067

    public void Dispose() { }
}
