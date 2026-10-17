using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace SongRequestDesktopV2Rewrite;

// ──────────────────────────────────────────────────────────────────────────────
//  MPRIS D-Bus service model (Linux).
//
//  The interfaces and property structs are intentionally public: Tmds.DBus
//  builds the exported adapter with Reflection.Emit in its own dynamic assembly,
//  which must be able to see the interfaces it implements.
// ──────────────────────────────────────────────────────────────────────────────

#pragma warning disable CS8618 // D-Bus property structs are only inspected for their shape.
[Dictionary]
public struct MprisRootProperties
{
    [Property(Access = PropertyAccess.Read)] public string Identity;
    [Property(Access = PropertyAccess.Read)] public string DesktopEntry;
    [Property(Access = PropertyAccess.Read)] public bool CanQuit;
    [Property(Access = PropertyAccess.Read)] public bool CanRaise;
    [Property(Access = PropertyAccess.Read)] public bool HasTrackList;
    [Property(Access = PropertyAccess.Read)] public string[] SupportedUriSchemes;
    [Property(Access = PropertyAccess.Read)] public string[] SupportedMimeTypes;
}

[Dictionary]
public struct MprisPlayerProperties
{
    [Property(Access = PropertyAccess.Read)] public string PlaybackStatus;
    [Property(Access = PropertyAccess.Read)] public string LoopStatus;
    [Property(Access = PropertyAccess.Read)] public double Rate;
    [Property(Access = PropertyAccess.Read)] public bool Shuffle;
    [Property(Access = PropertyAccess.Read)] public IDictionary<string, object> Metadata;
    [Property] public double Volume;
    [Property(Access = PropertyAccess.Read)] public long Position;
    [Property(Access = PropertyAccess.Read)] public double MinimumRate;
    [Property(Access = PropertyAccess.Read)] public double MaximumRate;
    [Property(Access = PropertyAccess.Read)] public bool CanGoNext;
    [Property(Access = PropertyAccess.Read)] public bool CanGoPrevious;
    [Property(Access = PropertyAccess.Read)] public bool CanPlay;
    [Property(Access = PropertyAccess.Read)] public bool CanPause;
    [Property(Access = PropertyAccess.Read)] public bool CanSeek;
    [Property(Access = PropertyAccess.Read)] public bool CanControl;
}
#pragma warning restore CS8618

[DBusInterface("org.mpris.MediaPlayer2", PropertyType = typeof(MprisRootProperties),
    GetPropertyMethod = "GetRootAsync", GetAllPropertiesMethod = "GetAllRootAsync",
    SetPropertyMethod = "SetRootAsync", WatchPropertiesMethod = "WatchRootPropertiesAsync")]
public interface IMprisRoot : IDBusObject
{
    Task RaiseAsync();
    Task QuitAsync();
    Task<object> GetRootAsync(string prop);
    Task<IDictionary<string, object>> GetAllRootAsync();
    Task SetRootAsync(string prop, object val);
    Task<IDisposable> WatchRootPropertiesAsync(Action<PropertyChanges> handler);
}

[DBusInterface("org.mpris.MediaPlayer2.Player", PropertyType = typeof(MprisPlayerProperties),
    GetPropertyMethod = "GetPlayerAsync", GetAllPropertiesMethod = "GetAllPlayerAsync",
    SetPropertyMethod = "SetPlayerAsync", WatchPropertiesMethod = "WatchPlayerPropertiesAsync")]
public interface IMprisPlayer : IDBusObject
{
    Task NextAsync();
    Task PreviousAsync();
    Task PauseAsync();
    Task PlayPauseAsync();
    Task StopAsync();
    Task PlayAsync();
    Task SeekAsync(long offsetMicroseconds);
    Task SetPositionAsync(ObjectPath trackId, long positionMicroseconds);
    Task OpenUriAsync(string uri);
    Task<object> GetPlayerAsync(string prop);
    Task<IDictionary<string, object>> GetAllPlayerAsync();
    Task SetPlayerAsync(string prop, object val);
    Task<IDisposable> WatchPlayerPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// Linux system-media integration through MPRIS
/// (<c>org.mpris.MediaPlayer2</c> over the session D-Bus). Desktop environments,
/// the media keys and tools like <c>playerctl</c> discover this service and can
/// show the current title/artist/artwork and drive playback.
/// </summary>
public sealed class MprisMediaSession : ISystemMediaSession, IMprisRoot, IMprisPlayer
{
    /// <summary>The well-known MPRIS service name registered on the session bus.</summary>
    public const string ServiceName = "org.mpris.MediaPlayer2.songrequest";

    /// <summary>The standard MPRIS object path.</summary>
    public static readonly ObjectPath RootObjectPath = new("/org/mpris/MediaPlayer2");

    private readonly Connection _connection;
    private readonly object _gate = new();
    private readonly List<Action<PropertyChanges>> _rootWatchers = new();
    private readonly List<Action<PropertyChanges>> _playerWatchers = new();

    private MediaMetadata? _metadata;
    private MediaPlaybackState _state = MediaPlaybackState.Stopped;
    private TimeSpan _position;
    private double _volume = 1.0;
    private long _trackId = 1;
    private bool _disposed;

    private MprisMediaSession(Connection connection, string serviceName)
    {
        _connection = connection;
        RegisteredServiceName = serviceName;
    }

    /// <summary>The service name this instance registered on the session bus.</summary>
    public string RegisteredServiceName { get; }

    /// <summary>
    /// Connects to the session bus and registers the MPRIS service, or returns
    /// null when no session bus is available.
    /// </summary>
    public static Task<MprisMediaSession?> TryCreateAsync() => TryCreateAsync(ServiceName);

    /// <summary>Connects with an explicit service name (used by tests).</summary>
    internal static async Task<MprisMediaSession?> TryCreateAsync(string serviceName)
    {
        Connection? connection = null;
        try
        {
            var address = Address.Session;
            if (string.IsNullOrEmpty(address)) return null;

            connection = new Connection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            var session = new MprisMediaSession(connection, serviceName);
            await connection.RegisterObjectAsync(session).ConfigureAwait(false);
            await connection.RegisterServiceAsync(serviceName).ConfigureAwait(false);
            return session;
        }
        catch
        {
            try { connection?.Dispose(); } catch { }
            return null;
        }
    }

    public bool IsSupported => true;

    public string BackendName => "MPRIS";

    public ObjectPath ObjectPath => RootObjectPath;

    // ── state pushed by the app ─────────────────────────────────────────────

    public void UpdateMetadata(MediaMetadata? metadata)
    {
        bool changed;
        lock (_gate)
        {
            changed = !Equals(_metadata, metadata);
            if (changed)
            {
                _metadata = metadata;
                _trackId++;
            }
        }

        if (changed)
            EmitPlayer(PropertyChanges.ForProperty("Metadata", BuildMetadata()));
    }

    public void UpdatePlayback(MediaPlaybackState state, TimeSpan position)
    {
        bool stateChanged;
        lock (_gate)
        {
            stateChanged = _state != state;
            _state = state;
            _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }

        if (stateChanged)
            EmitPlayer(PropertyChanges.ForProperty("PlaybackStatus", PlaybackStatusString(state)));
    }

    // ── transport commands raised for the app ───────────────────────────────

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

    public Task PlayAsync() { PlayRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task PauseAsync() { PauseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task PlayPauseAsync() { TogglePlayPauseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task NextAsync() { NextRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task PreviousAsync() { PreviousRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task StopAsync() { StopRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }

    public Task SeekAsync(long offsetMicroseconds)
    {
        SeekRequested?.Invoke(this, TimeSpan.FromMicroseconds(offsetMicroseconds));
        return Task.CompletedTask;
    }

    public Task SetPositionAsync(ObjectPath trackId, long positionMicroseconds)
    {
        PositionSetRequested?.Invoke(this, TimeSpan.FromMicroseconds(positionMicroseconds));
        return Task.CompletedTask;
    }

    /// <summary>Opening a URI is not supported; ignored (kept for MPRIS compliance).</summary>
    public Task OpenUriAsync(string uri) => Task.CompletedTask;

    public Task RaiseAsync() { RaiseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }

    public Task QuitAsync() { QuitRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }

    // ── org.mpris.MediaPlayer2 (root) properties ────────────────────────────

    public Task<object> GetRootAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "Identity" => "SongRequest V2",
        "DesktopEntry" => "songrequest",
        "CanQuit" => false,
        "CanRaise" => false,
        "HasTrackList" => false,
        "SupportedUriSchemes" => Array.Empty<string>(),
        "SupportedMimeTypes" => Array.Empty<string>(),
        _ => throw new ArgumentException($"Unknown MPRIS property '{prop}'"),
    });

    public Task<IDictionary<string, object>> GetAllRootAsync() => Task.FromResult<IDictionary<string, object>>(
        new Dictionary<string, object>
        {
            ["Identity"] = "SongRequest V2",
            ["DesktopEntry"] = "songrequest",
            ["CanQuit"] = false,
            ["CanRaise"] = false,
            ["HasTrackList"] = false,
            ["SupportedUriSchemes"] = Array.Empty<string>(),
            ["SupportedMimeTypes"] = Array.Empty<string>(),
        });

    public Task SetRootAsync(string prop, object val) => Task.CompletedTask;

    // ── org.mpris.MediaPlayer2.Player properties ────────────────────────────

    public Task<object> GetPlayerAsync(string prop) => Task.FromResult(GetPlayerProperty(prop));

    public Task<IDictionary<string, object>> GetAllPlayerAsync() => Task.FromResult<IDictionary<string, object>>(
        new Dictionary<string, object>
        {
            ["PlaybackStatus"] = PlaybackStatusString(_state),
            ["LoopStatus"] = "None",
            ["Rate"] = 1.0,
            ["Shuffle"] = false,
            ["Metadata"] = BuildMetadata(),
            ["Volume"] = _volume,
            ["Position"] = ToMicroseconds(_position),
            ["MinimumRate"] = 1.0,
            ["MaximumRate"] = 1.0,
            ["CanGoNext"] = true,
            ["CanGoPrevious"] = true,
            ["CanPlay"] = true,
            ["CanPause"] = true,
            ["CanSeek"] = true,
            ["CanControl"] = true,
        });

    public Task SetPlayerAsync(string prop, object val)
    {
        if (string.Equals(prop, "Volume", StringComparison.Ordinal) && TryToDouble(val, out var volume))
        {
            volume = Math.Clamp(volume, 0.0, 1.0);
            lock (_gate) { _volume = volume; }
            VolumeRequested?.Invoke(this, volume);
        }

        return Task.CompletedTask;
    }

    // ── property change subscriptions (the adapter re-emits these as signals) ─

    public Task<IDisposable> WatchRootPropertiesAsync(Action<PropertyChanges> handler)
    {
        lock (_gate) { _rootWatchers.Add(handler); }
        return Task.FromResult<IDisposable>(new Subscription(() => { lock (_gate) { _rootWatchers.Remove(handler); } }));
    }

    public Task<IDisposable> WatchPlayerPropertiesAsync(Action<PropertyChanges> handler)
    {
        lock (_gate) { _playerWatchers.Add(handler); }
        return Task.FromResult<IDisposable>(new Subscription(() => { lock (_gate) { _playerWatchers.Remove(handler); } }));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private object GetPlayerProperty(string prop)
    {
        lock (_gate)
        {
            return prop switch
            {
                "PlaybackStatus" => PlaybackStatusString(_state),
                "LoopStatus" => "None",
                "Rate" => 1.0,
                "Shuffle" => false,
                "Metadata" => BuildMetadata(),
                "Volume" => _volume,
                "Position" => ToMicroseconds(_position),
                "MinimumRate" => 1.0,
                "MaximumRate" => 1.0,
                "CanGoNext" => true,
                "CanGoPrevious" => true,
                "CanPlay" => true,
                "CanPause" => true,
                "CanSeek" => true,
                "CanControl" => true,
                _ => throw new ArgumentException($"Unknown MPRIS property '{prop}'"),
            };
        }
    }

    private IDictionary<string, object> BuildMetadata()
    {
        var metadata = new Dictionary<string, object>
        {
            ["mpris:trackid"] = new ObjectPath($"/org/mpris/MediaPlayer2/Track/{_trackId}"),
        };

        var current = _metadata;
        if (current != null)
        {
            if (!string.IsNullOrEmpty(current.Title)) metadata["xesam:title"] = current.Title;
            if (!string.IsNullOrEmpty(current.Artist)) metadata["xesam:artist"] = new[] { current.Artist };
            if (!string.IsNullOrEmpty(current.Album)) metadata["xesam:album"] = current.Album;
            if (current.Duration > TimeSpan.Zero) metadata["mpris:length"] = ToMicroseconds(current.Duration);
            if (!string.IsNullOrEmpty(current.ArtworkPath))
            {
                var uri = current.ArtworkPath;
                if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    uri = "file://" + uri.Replace("\\", "/");
                metadata["mpris:artUrl"] = uri;
            }
        }

        return metadata;
    }

    private void EmitPlayer(PropertyChanges changes)
    {
        Action<PropertyChanges>[] watchers;
        lock (_gate) { watchers = _playerWatchers.ToArray(); }
        foreach (var watcher in watchers)
        {
            try { watcher(changes); } catch { }
        }
    }

    private static string PlaybackStatusString(MediaPlaybackState state) => state switch
    {
        MediaPlaybackState.Playing => "Playing",
        MediaPlaybackState.Paused => "Paused",
        _ => "Stopped",
    };

    private static long ToMicroseconds(TimeSpan value) => (long)(value.TotalSeconds * 1_000_000d);

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            default: result = 0; return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _connection.UnregisterObject(RootObjectPath); } catch { }
        try { _connection.Dispose(); } catch { }

        lock (_gate)
        {
            _rootWatchers.Clear();
            _playerWatchers.Clear();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            var action = _dispose;
            _dispose = null;
            action?.Invoke();
        }
    }
}
