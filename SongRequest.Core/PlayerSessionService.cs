using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Persisted player snapshot. Additive/versioned so old files remain readable.</summary>
public sealed class PlayerSessionState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    public string? CurrentSongPath { get; set; }
    public double PositionSeconds { get; set; }
    public bool WasPlaying { get; set; }

    public double Volume { get; set; } = 0.9;
    public double CrossfadeSeconds { get; set; } = 4;
    public int OutputDeviceNumber { get; set; } = -1;
    public bool RepeatEnabled { get; set; }

    public List<PlayerSessionSong> Queue { get; set; } = new();
    public List<PlayerSessionSong> PlaylistQueue { get; set; } = new();
    public List<PlayerSessionSong> History { get; set; } = new();
}

/// <summary>A queue entry without opaque thumbnails or live audio objects.</summary>
public sealed class PlayerSessionSong
{
    public string SongPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public double PerceivedLoudness { get; set; } = -1;
}

/// <summary>
/// Atomic, failure-tolerant player-session file. A corrupt or partially written
/// file is ignored rather than allowed to break startup.
/// </summary>
public sealed class PlayerSessionStore
{
    public string FilePath { get; }

    public PlayerSessionStore(string filePath) => FilePath = filePath;

    public static PlayerSessionStore Default()
        => new(Path.Combine(AppPaths.AppDataDir, "player-session.json"));

    public PlayerSessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 8 * 1024 * 1024) return null;
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = JsonConvert.DeserializeObject<PlayerSessionState>(json);
            if (state == null) return null;
            if (state.Version <= 0 || state.Version > PlayerSessionState.CurrentVersion) return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes to a temp file and renames over the target, so a crash can't truncate the session.</summary>
    public bool Save(PlayerSessionState state)
    {
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            state.SavedAtUtc = DateTime.UtcNow;
            state.Version = PlayerSessionState.CurrentVersion;
            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    public void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}

/// <summary>
/// Keeps the player's queue, current track, position and previous-track history
/// across restarts. Saves are debounced/periodic, atomic, and skipped while a
/// restore is applying state. Restore failures never prevent startup.
/// </summary>
public sealed class PlayerSessionService : IDisposable
{
    private const int SaveIntervalMs = 5000;

    private readonly MusicEngine _engine;
    private readonly PlayerSessionStore _store;
    private readonly Timer _timer;
    private int _dirty;
    private int _restoring;
    private int _disposed;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public PlayerSessionService(MusicEngine engine, PlayerSessionStore? store = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? PlayerSessionStore.Default();

        _engine.NowPlayingChanged += OnNowPlayingChanged;
        _engine.PlayStateChanged += OnPlayStateChanged;
        _engine.QueueChanged += OnChanged;
        _engine.PlaylistQueueChanged += OnChanged;
        _engine.HistoryChanged += OnChanged;
        _engine.PositionChanged += OnPositionChanged;

        _timer = new Timer(_ =>
        {
            try
            {
                var dirty = Volatile.Read(ref _dirty) != 0;
                var stale = DateTime.UtcNow - _lastSaveUtc > TimeSpan.FromSeconds(30);
                if ((dirty || stale) && Volatile.Read(ref _restoring) == 0)
                    Flush();
            }
            catch { }
        }, null, SaveIntervalMs, SaveIntervalMs);
    }

    public bool RestoreCompleted { get; private set; }
    public string? LastError { get; private set; }

    private void OnChanged(object? sender, EventArgs e) => MarkDirty();
    private void OnNowPlayingChanged(Song? song) => MarkDirty();
    private void OnPlayStateChanged(bool playing) => MarkDirty();
    private void OnPositionChanged(double normalized, TimeSpan current, TimeSpan remaining) => MarkDirty();
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// Loads and applies a saved session. The restored track is opened paused at
    /// the saved position; a session file that references missing files is pruned
    /// instead of failing startup.
    /// </summary>
    public async Task<bool> RestoreAsync()
    {
        if (Interlocked.Exchange(ref _restoring, 1) == 1) return false;
        try
        {
            var state = _store.Load();
            if (state == null)
            {
                RestoreCompleted = true;
                return false;
            }

            var queue = BuildSongs(state.Queue, out _);
            var playlistQueue = BuildSongs(state.PlaylistQueue, out _);
            var history = BuildSongs(state.History, out _);

            var currentPath = AppPaths.ResolvePortablePath(state.CurrentSongPath);
            Song? current = queue.FirstOrDefault(s => SamePath(s.songPath, currentPath));
            if (current == null && !string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
            {
                current = new Song(
                    Path.GetFileNameWithoutExtension(currentPath),
                    "Unknown",
                    null,
                    TimeSpan.Zero,
                    currentPath);
                queue.Insert(0, current);
            }

            // A session without an explicit current track but with a queue should
            // still restore the queue in a usable paused state.
            if (current == null && queue.Count > 0)
                current = queue[0];

            if (current != null)
            {
                var index = queue.FindIndex(s => SamePath(s.songPath, current.songPath));
                if (index > 0)
                {
                    queue.RemoveAt(index);
                    queue.Insert(0, current);
                }
            }

            using (_engine.BeginBulkUpdate())
            {
                _engine.Queue.Clear();
                foreach (var song in queue) _engine.Queue.Add(song);
                _engine.PlaylistQueue.Clear();
                foreach (var song in playlistQueue) _engine.PlaylistQueue.Add(song);
            }

            _engine.RestorePlaybackHistory(history);
            _engine.SetVolume((float)Math.Clamp(state.Volume, 0.0, 1.0));
            _engine.SetCrossfade(Math.Clamp(state.CrossfadeSeconds, 0.0, _engine.CrossfadeMax));
            _engine.OutputDeviceNumber = state.OutputDeviceNumber;
            _engine.RepeatEnabled = state.RepeatEnabled;
            _engine.ComputeQueueTimings();

            if (current != null)
            {
                var position = TimeSpan.FromSeconds(Math.Max(0, state.PositionSeconds));
                await _engine.RestoreCurrentSongAsync(current, position, autoPlay: false).ConfigureAwait(false);
            }

            RestoreCompleted = true;
            Interlocked.Exchange(ref _dirty, 0);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RestoreCompleted = true;
            return false;
        }
        finally
        {
            Volatile.Write(ref _restoring, 0);
        }
    }

    /// <summary>Writes the current snapshot immediately. Never throws.</summary>
    public bool Flush()
    {
        if (Volatile.Read(ref _restoring) != 0) return false;
        Interlocked.Exchange(ref _dirty, 0);
        try
        {
            var ok = _store.Save(Capture());
            if (!ok) Interlocked.Exchange(ref _dirty, 1);
            else _lastSaveUtc = DateTime.UtcNow;
            return ok;
        }
        catch
        {
            Interlocked.Exchange(ref _dirty, 1);
            return false;
        }
    }

    private PlayerSessionState Capture()
    {
        var current = _engine.CurrentSong;
        var position = _engine.CurrentPosition;

        return new PlayerSessionState
        {
            CurrentSongPath = current == null ? null : AppPaths.ToPortablePath(current.songPath),
            PositionSeconds = position.TotalSeconds,
            WasPlaying = _engine.CurrentIsPlaying,
            Volume = _engine.VolumeValue,
            CrossfadeSeconds = _engine.CrossfadeSeconds,
            OutputDeviceNumber = _engine.OutputDeviceNumber,
            RepeatEnabled = _engine.RepeatEnabled,
            Queue = _engine.Queue.Select(ToSessionSong).ToList(),
            PlaylistQueue = _engine.PlaylistQueue.Select(ToSessionSong).ToList(),
            History = _engine.GetPlaybackHistory().Select(ToSessionSong).ToList(),
        };
    }

    private static PlayerSessionSong ToSessionSong(Song song) => new()
    {
        SongPath = AppPaths.ToPortablePath(song.songPath),
        Title = song.Title ?? string.Empty,
        Artist = song.Artist ?? string.Empty,
        DurationSeconds = song.Duration.TotalSeconds,
        PerceivedLoudness = song.PerceivedLoudness,
    };

    private static List<Song> BuildSongs(IEnumerable<PlayerSessionSong>? saved, out int skipped)
    {
        skipped = 0;
        var list = new List<Song>();
        if (saved == null) return list;

        foreach (var entry in saved)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.SongPath))
            {
                skipped++;
                continue;
            }

            var path = AppPaths.ResolvePortablePath(entry.SongPath);
            if (!File.Exists(path))
            {
                skipped++;
                continue;
            }

            var duration = TimeSpan.FromSeconds(Math.Max(0, entry.DurationSeconds));
            var song = new Song(
                string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileNameWithoutExtension(path) : entry.Title,
                string.IsNullOrWhiteSpace(entry.Artist) ? "Unknown" : entry.Artist,
                null,
                duration,
                path)
            {
                PerceivedLoudness = entry.PerceivedLoudness,
            };
            list.Add(song);
        }

        return list;
    }

    private static bool SamePath(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _engine.NowPlayingChanged -= OnNowPlayingChanged; } catch { }
        try { _engine.PlayStateChanged -= OnPlayStateChanged; } catch { }
        try { _engine.QueueChanged -= OnChanged; } catch { }
        try { _engine.PlaylistQueueChanged -= OnChanged; } catch { }
        try { _engine.HistoryChanged -= OnChanged; } catch { }
        try { _engine.PositionChanged -= OnPositionChanged; } catch { }
        try { _timer.Dispose(); } catch { }
        Flush();
    }
}
