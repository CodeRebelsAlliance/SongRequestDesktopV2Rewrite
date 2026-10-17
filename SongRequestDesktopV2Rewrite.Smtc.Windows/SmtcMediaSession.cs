using System;
using System.IO;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

namespace SongRequestDesktopV2Rewrite.Smtc;

/// <summary>
/// Windows system media integration via **System Media Transport Controls**
/// (SMTC): the Windows media overlay, media keys, lock screen and taskbar now
/// show the current title/artist/album/artwork and can drive playback.
///
/// This type lives in the Windows-only companion assembly
/// (<c>SongRequestDesktopV2Rewrite.Smtc.Windows</c>, TFM
/// <c>net10.0-windows10.0.19041.0</c>) because WinRT requires the CsWinRT
/// projection. The main cross-platform app loads it at runtime through
/// <c>Platform/Media/WindowsSmtcLoader.cs</c>; see
/// <c>docs/SYSTEM-MEDIA-INTEGRATION.md</c>.
/// </summary>
public sealed class SmtcMediaSession : ISystemMediaSession
{
    private const int SeekStepSeconds = 10;

    private readonly SystemMediaTransportControls _controls;
    private readonly TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs> _buttonPressed;
    private readonly TypedEventHandler<SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs> _positionChangeRequested;
    private readonly object _gate = new();

    private MediaMetadata? _metadata;
    private TimeSpan _lastTimelinePosition = TimeSpan.MinValue;
    private bool _disposed;

    private SmtcMediaSession(SystemMediaTransportControls controls)
    {
        _controls = controls;
        _buttonPressed = OnButtonPressed;
        _positionChangeRequested = OnPlaybackPositionChangeRequested;

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsFastForwardEnabled = true;
        _controls.IsRewindEnabled = true;

        _controls.ButtonPressed += _buttonPressed;
        _controls.PlaybackPositionChangeRequested += _positionChangeRequested;
    }

    /// <summary>
    /// Entry point invoked by the main app via reflection with the top-level
    /// window handle. Returns null when SMTC cannot be attached to the window.
    /// </summary>
    public static ISystemMediaSession? Create(IntPtr windowHandle)
    {
        try
        {
            if (windowHandle == IntPtr.Zero) return null;

            var controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            if (controls == null) return null;

            return new SmtcMediaSession(controls);
        }
        catch
        {
            // No media overlay / unsupported OS: the app falls back to no session.
            return null;
        }
    }

    public bool IsSupported => true;

    public string BackendName => "SMTC";

    // SMTC delivers Play and Pause as distinct buttons, exposes seeking through
    // PlaybackPositionChangeRequested, and has no app-volume / raise / quit
    // concept, so those events are never raised here.
#pragma warning disable CS0067
    public event EventHandler? TogglePlayPauseRequested;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler? RaiseRequested;
    public event EventHandler? QuitRequested;
#pragma warning restore CS0067

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? PositionSetRequested;

    public void UpdateMetadata(MediaMetadata? metadata)
    {
        lock (_gate) { _metadata = metadata; }
        ApplyMetadata(metadata);
    }

    public void UpdatePlayback(MediaPlaybackState state, TimeSpan position)
    {
        try
        {
            _controls.PlaybackStatus = state switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
        catch { }

        TimeSpan duration;
        lock (_gate) { duration = _metadata?.Duration ?? TimeSpan.Zero; }

        // Timeline updates cross into the OS; only push them when the position
        // has moved appreciably (or jumped backwards / on the first update).
        bool shouldUpdate;
        lock (_gate)
        {
            shouldUpdate = _lastTimelinePosition == TimeSpan.MinValue
                || position < _lastTimelinePosition
                || (position - _lastTimelinePosition).Duration() >= TimeSpan.FromSeconds(1);
            if (shouldUpdate) _lastTimelinePosition = position;
        }

        if (!shouldUpdate) return;

        try
        {
            _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = duration,
                MaxSeekTime = duration,
                Position = position,
            });
        }
        catch { }
    }

    private void ApplyMetadata(MediaMetadata? metadata)
    {
        try
        {
            var updater = _controls.DisplayUpdater;
            if (metadata == null)
            {
                updater.ClearAll();
                updater.Update();
                return;
            }

            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = "SongRequest";

            var music = updater.MusicProperties;
            music.Title = metadata.Title ?? string.Empty;
            music.Artist = metadata.Artist ?? string.Empty;
            music.AlbumTitle = metadata.Album ?? string.Empty;

            updater.Thumbnail = CreateThumbnail(metadata.ArtworkPath);
            updater.Update();
        }
        catch { }
    }

    private static RandomAccessStreamReference? CreateThumbnail(string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath)) return null;
        try
        {
            if (!File.Exists(artworkPath)) return null;
            var uri = new Uri(Path.GetFullPath(artworkPath));
            if (!uri.IsAbsoluteUri) return null;
            return RandomAccessStreamReference.CreateFromUri(uri);
        }
        catch
        {
            return null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                PlayRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Pause:
                PauseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Stop:
                StopRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.FastForward:
                SeekRequested?.Invoke(this, TimeSpan.FromSeconds(SeekStepSeconds));
                break;
            case SystemMediaTransportControlsButton.Rewind:
                SeekRequested?.Invoke(this, TimeSpan.FromSeconds(-SeekStepSeconds));
                break;
        }
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        PositionSetRequested?.Invoke(this, args.RequestedPlaybackPosition);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _controls.ButtonPressed -= _buttonPressed; } catch { }
        try { _controls.PlaybackPositionChangeRequested -= _positionChangeRequested; } catch { }
        try { _controls.DisplayUpdater.ClearAll(); _controls.DisplayUpdater.Update(); } catch { }
        try { _controls.IsEnabled = false; } catch { }

        lock (_gate)
        {
            _metadata = null;
            _lastTimelinePosition = TimeSpan.MinValue;
        }
    }
}
