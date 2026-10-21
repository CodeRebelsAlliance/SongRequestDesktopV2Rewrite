using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Bridges the app's <see cref="MusicEngine"/> to a system media session
/// (MPRIS/SMTC/...): it publishes the now-playing metadata, artwork, playback
/// state and position, and turns OS transport commands back into engine calls.
///
/// The bridge contains no platform code, so it is unit-tested against a fake
/// <see cref="ISystemMediaSession"/>.
/// </summary>
public sealed class MediaSessionBridge : IDisposable
{
    private readonly MusicEngine _engine;
    private readonly ISystemMediaSession _session;
    private readonly Func<Song?, string?>? _artworkResolver;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();

    private Song? _lastSong;
    private MediaMetadata? _lastMetadata;
    private bool _hasMetadata;
    private bool _disposed;
    private DateTime _artworkRetryAtUtc;
    private int _artworkRetries;

    /// <summary>Raised when the OS asks to surface the app window (MPRIS <c>Raise</c>).</summary>
    public event EventHandler? RaiseRequested;

    /// <summary>Raised when the OS asks to quit the app (MPRIS <c>Quit</c>).</summary>
    public event EventHandler? QuitRequested;

    public MediaSessionBridge(
        MusicEngine engine,
        ISystemMediaSession session,
        Func<Song?, string?>? artworkResolver = null,
        Action<Action>? dispatch = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _artworkResolver = artworkResolver;
        _dispatch = dispatch ?? (action => action());

        _engine.NowPlayingTick += OnNowPlayingTick;
        _engine.QueueChanged += OnQueueChanged;
        _engine.QueuesChanged += OnQueueChanged;
        _engine.PlayStateChanged += OnPlayStateChanged;

        _session.PlayRequested += OnPlayRequested;
        _session.PauseRequested += OnPauseRequested;
        _session.TogglePlayPauseRequested += OnTogglePlayPauseRequested;
        _session.NextRequested += OnNextRequested;
        _session.PreviousRequested += OnPreviousRequested;
        _session.StopRequested += OnStopRequested;
        _session.SeekRequested += OnSeekRequested;
        _session.PositionSetRequested += OnPositionSetRequested;
        _session.VolumeRequested += OnVolumeRequested;
        _session.RaiseRequested += OnRaiseRequested;
        _session.QuitRequested += OnQuitRequested;
    }

    /// <summary>The backend name (e.g. "MPRIS"), for status reporting.</summary>
    public string BackendName => _session.BackendName;

    /// <summary>Pushes the current engine state to the session (used on startup/resume).</summary>
    public void Refresh()
    {
        var song = CurrentSong();
        _lastSong = song;
        PushMetadata(song);
        PushState();
    }

    // ── engine → session ────────────────────────────────────────────────────

    private void OnNowPlayingTick(object? sender, NowPlayingEventArgs e)
    {
        var song = CurrentSong();
        if (!ReferenceEquals(song, _lastSong))
        {
            _lastSong = song;
            _artworkRetries = 0;
            _artworkRetryAtUtc = DateTime.UtcNow.AddSeconds(2);
            PushMetadata(song);
        }
        else if (song != null &&
                 string.IsNullOrEmpty(_lastMetadata?.ArtworkPath) &&
                 _artworkRetries < 10 &&
                 DateTime.UtcNow >= _artworkRetryAtUtc)
        {
            // Artwork may be written/converted a moment after playback starts.
            // Metadata used to be pushed only on the first tick, so the OS media
            // controls (KDE/MPRIS in particular) never got the cover at all.
            _artworkRetries++;
            _artworkRetryAtUtc = DateTime.UtcNow.AddSeconds(3);
            PushMetadata(song);
        }

        PushState();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        var song = CurrentSong();
        if (!ReferenceEquals(song, _lastSong))
        {
            _lastSong = song;
            PushMetadata(song);
        }
    }

    private void OnPlayStateChanged(bool playing)
    {
        // The engine reports play state through a field, not an event carrying the
        // full picture; re-derive from the engine so pause/stop are reported too.
        _ = playing;
        PushState();
    }

    private Song? CurrentSong() => _engine.Queue.Count > 0 ? _engine.Queue[0] : null;

    private void PushMetadata(Song? song)
    {
        string? artwork = null;
        if (song != null && _artworkResolver != null)
        {
            try { artwork = _artworkResolver(song); } catch { }
        }

        var metadata = song == null
            ? null
            : new MediaMetadata(
                song.Title ?? string.Empty,
                song.Artist ?? string.Empty,
                string.Empty,
                song.Duration,
                artwork);

        lock (_gate)
        {
            if (_hasMetadata && Equals(metadata, _lastMetadata)) return;
            _lastMetadata = metadata;
            _hasMetadata = true;
        }

        _session.UpdateMetadata(metadata);
    }

    private void PushState()
    {
        var state = _engine.IsPlaying ? MediaPlaybackState.Playing
            : _engine.IsPlaybackActive ? MediaPlaybackState.Paused
            : MediaPlaybackState.Stopped;

        var position = _engine.CurrentReader?.CurrentTime ?? TimeSpan.Zero;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        _session.UpdatePlayback(state, position);
    }

    // ── session → engine ────────────────────────────────────────────────────

    private void OnPlayRequested(object? sender, EventArgs e) => _dispatch(() =>
    {
        if (_engine.IsPlaying) return;
        if (_engine.Output == null)
        {
            if (_engine.Queue.Count > 0) _engine.PlaySong(_engine.Queue[0]);
            return;
        }

        _engine.TogglePlayPause();
    });

    private void OnPauseRequested(object? sender, EventArgs e) => _dispatch(() =>
    {
        if (_engine.IsPlaying) _engine.TogglePlayPause();
    });

    private void OnTogglePlayPauseRequested(object? sender, EventArgs e) => _dispatch(() => _engine.TogglePlayPause());

    private void OnNextRequested(object? sender, EventArgs e) => _dispatch(() => _engine.Next());

    private void OnPreviousRequested(object? sender, EventArgs e) => _dispatch(() => _engine.Previous());

    private void OnStopRequested(object? sender, EventArgs e) => _dispatch(() => _engine.Stop());

    private void OnSeekRequested(object? sender, TimeSpan offset) => _dispatch(() =>
    {
        var reader = _engine.CurrentReader;
        if (reader == null) return;
        SeekToAbsolute(reader.CurrentTime + offset);
    });

    private void OnPositionSetRequested(object? sender, TimeSpan position) => _dispatch(() => SeekToAbsolute(position));

    private void OnVolumeRequested(object? sender, double volume) => _dispatch(() => _engine.SetVolume((float)volume));

    private void OnRaiseRequested(object? sender, EventArgs e) => RaiseRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitRequested(object? sender, EventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void SeekToAbsolute(TimeSpan absolute)
    {
        var reader = _engine.CurrentReader;
        if (reader == null) return;

        var total = reader.TotalTime.TotalSeconds;
        if (total <= 0) return;

        _engine.Seek(absolute.TotalSeconds / total);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _engine.NowPlayingTick -= OnNowPlayingTick;
            _engine.QueueChanged -= OnQueueChanged;
            _engine.QueuesChanged -= OnQueueChanged;
            _engine.PlayStateChanged -= OnPlayStateChanged;
        }
        catch { }

        try
        {
            _session.PlayRequested -= OnPlayRequested;
            _session.PauseRequested -= OnPauseRequested;
            _session.TogglePlayPauseRequested -= OnTogglePlayPauseRequested;
            _session.NextRequested -= OnNextRequested;
            _session.PreviousRequested -= OnPreviousRequested;
            _session.StopRequested -= OnStopRequested;
            _session.SeekRequested -= OnSeekRequested;
            _session.PositionSetRequested -= OnPositionSetRequested;
            _session.VolumeRequested -= OnVolumeRequested;
            _session.RaiseRequested -= OnRaiseRequested;
            _session.QuitRequested -= OnQuitRequested;
        }
        catch { }

        try { _session.Dispose(); } catch { }
    }
}
