using System;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using SongRequestDesktopV2Rewrite;
using AndroidMetadata = Android.Media.MediaMetadata;
using CoreMetadata = SongRequestDesktopV2Rewrite.MediaMetadata;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;

namespace SongRequest.Mobile;

/// <summary>
/// Android system-media backend: the framework <c>MediaSession</c> (lock screen,
/// notification, headset buttons, Bluetooth AVRCP) plus a foreground playback
/// service so audio keeps running while the app is backgrounded.
///
/// The core's <c>MediaSessionBridge</c> drives this: it pushes metadata and
/// playback state, and listens to the transport events raised here.
/// </summary>
internal sealed class AndroidMediaSessionBackend : ISystemMediaSessionBackend
{
    public bool IsSupported => true;

    public string Name => "MediaSession";

    public Task<ISystemMediaSession?> TryCreateAsync(IntPtr windowHandle)
        => Task.FromResult<ISystemMediaSession?>(new AndroidSystemMediaSession());
}

/// <summary>The live session; one instance per app run.</summary>
internal sealed class AndroidSystemMediaSession : ISystemMediaSession
{
    private readonly MediaSession? _session;
    private AndroidMetadata? _metadata;
    private MediaPlaybackState _state = MediaPlaybackState.Stopped;
    private TimeSpan _position;
    private bool _disposed;

    public AndroidSystemMediaSession()
    {
        try
        {
            var context = Android.App.Application.Context;
            PlaybackService.EnsureChannel(context);

            _session = new MediaSession(context, "SongRequest");
            _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
            _session.SetCallback(new TransportCallback(this));
            _session.Active = true;
            AndroidSessionHolder.Session = _session;
            AndroidSessionHolder.Current = _session.SessionToken;

            // Notification actions arrive at the service, which forwards them here.
            PlaybackService.CommandReceived += OnServiceCommand;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"MediaSession unavailable: {ex.Message}");
            _session = null;
        }
    }

    public bool IsSupported => _session != null;

    public string BackendName => _session != null ? "MediaSession" : "none";

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? TogglePlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

#pragma warning disable CS0067 // Android has no equivalent: no OS mixer, no raise/set-position requests.
    public event EventHandler<TimeSpan>? PositionSetRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler? RaiseRequested;
#pragma warning restore CS0067

    public event EventHandler? QuitRequested;

    public void UpdateMetadata(CoreMetadata? metadata)
    {
        var session = _session;
        if (session == null) return;

        try
        {
            if (metadata == null)
            {
                _metadata = null;
                session.SetMetadata(null);
                PlaybackService.Update(null, _state, _position);
                return;
            }

            var builder = new AndroidMetadata.Builder();
            builder.PutString(AndroidMetadata.MetadataKeyTitle, metadata.Title);
            builder.PutString(AndroidMetadata.MetadataKeyArtist, metadata.Artist);
            builder.PutString(AndroidMetadata.MetadataKeyAlbum, metadata.Album);
            builder.PutLong(AndroidMetadata.MetadataKeyDuration, (long)metadata.Duration.TotalMilliseconds);

            var mediaId = AndroidSessionHolder.CurrentMediaId;
            if (!string.IsNullOrEmpty(mediaId))
                builder.PutString(AndroidMetadata.MetadataKeyMediaId, mediaId);

            var artwork = TryLoadArtwork(metadata.ArtworkPath);
            if (artwork != null)
                builder.PutBitmap(AndroidMetadata.MetadataKeyAlbumArt, artwork);

            _metadata = builder.Build();
            session.SetMetadata(_metadata);
            PlaybackService.Update(_metadata, _state, _position);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"MediaSession metadata failed: {ex.Message}");
        }
    }

    public void UpdatePlayback(MediaPlaybackState state, TimeSpan position)
    {
        var session = _session;
        if (session == null) return;

        try
        {
            _state = state;
            _position = position;

            var code = state switch
            {
                MediaPlaybackState.Playing => PlaybackStateCode.Playing,
                MediaPlaybackState.Paused => PlaybackStateCode.Paused,
                _ => PlaybackStateCode.Stopped,
            };

            var playback = new AndroidPlaybackState.Builder();
            playback.SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause |
                                PlaybackState.ActionPlayPause | PlaybackState.ActionSkipToNext |
                                PlaybackState.ActionSkipToPrevious | PlaybackState.ActionStop |
                                PlaybackState.ActionSeekTo);
            playback.SetState(code, (long)position.TotalMilliseconds, 1f);

            session.SetPlaybackState(playback.Build());

            // Keep audio alive in the background while playing; release the
            // foreground service when it stops (Android 12+ is strict about this).
            if (state == MediaPlaybackState.Stopped)
            {
                PlaybackService.Update(_metadata, state, position);
                PlaybackService.Stop(Android.App.Application.Context);
            }
            else
            {
                PlaybackService.Start(Android.App.Application.Context);
                PlaybackService.Update(_metadata, state, position);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"MediaSession state failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PlaybackService.CommandReceived -= OnServiceCommand;

        AndroidSessionHolder.Current = null;
        AndroidSessionHolder.Session = null;
        try { if (_session != null) _session.Active = false; } catch { }
        try { _session?.Release(); } catch { }
        try { PlaybackService.Stop(Android.App.Application.Context); } catch { }
    }

    private void OnServiceCommand(object? sender, string command)
    {
        switch (command)
        {
            case PlaybackService.ActionPlay: PlayRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionPause: PauseRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionToggle: TogglePlayPauseRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionNext: NextRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionPrevious: PreviousRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionStop: StopRequested?.Invoke(this, EventArgs.Empty); break;
            case PlaybackService.ActionQuit: QuitRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private static Bitmap? TryLoadArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        try { return MediaArtwork.Load(path); }
        catch { return null; }
    }

    /// <summary>Lock-screen / headset / Bluetooth transport callbacks.</summary>
    private sealed class TransportCallback : MediaSession.Callback
    {
        private readonly AndroidSystemMediaSession _owner;

        public TransportCallback(AndroidSystemMediaSession owner) => _owner = owner;

        public override void OnPlay() => _owner.PlayRequested?.Invoke(_owner, EventArgs.Empty);

        public override void OnPause() => _owner.PauseRequested?.Invoke(_owner, EventArgs.Empty);

        public override void OnStop() => _owner.StopRequested?.Invoke(_owner, EventArgs.Empty);

        public override void OnSkipToNext() => _owner.NextRequested?.Invoke(_owner, EventArgs.Empty);

        public override void OnSkipToPrevious() => _owner.PreviousRequested?.Invoke(_owner, EventArgs.Empty);

        public override void OnSeekTo(long pos)
            => _owner.SeekRequested?.Invoke(_owner, TimeSpan.FromMilliseconds(pos));

        public override void OnPlayFromMediaId(string? mediaId, Bundle? extras)
        {
            if (!string.IsNullOrWhiteSpace(mediaId))
                CarMediaBridge.PlayMediaId(mediaId);
        }

        public override void OnPlayFromSearch(string? query, Bundle? extras)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            var match = CarMediaBridge.SearchFor(query).FirstOrDefault();
            if (match != null) CarMediaBridge.PlayMediaId(match.Id);
        }
    }
}
