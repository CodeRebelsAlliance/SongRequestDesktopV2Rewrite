using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Serializable progress/state for the background library analysis.</summary>
public sealed class LibraryAnalysisProgress
{
    public bool IsRunning { get; init; }
    public bool IsStopping { get; init; }
    public bool PausedForPlayback { get; init; }
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Analyzed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public double Progress { get; init; }
    public double CurrentSongProgress { get; init; }
    public string CurrentSong { get; init; } = "";
    public string Status { get; init; } = "idle";
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
}

/// <summary>
/// Runs low-priority analysis over the library, one song at a time. New library
/// songs can be queued while it runs, and the worker yields while music is
/// playing so playback stays fluent.
/// </summary>
public sealed class LibraryAnalysisService : IDisposable
{
    private readonly LibraryService _library;
    private readonly SongAnalysisService _analysis;
    private readonly object _sync = new();
    private readonly ConcurrentQueue<LibrarySong> _queue = new();
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _force;
    private bool _fullScan;
    private bool _stopping;
    private bool _paused;
    private int _total;
    private int _completed;
    private int _analyzed;
    private int _failed;
    private int _skipped;
    private string _currentSong = "";
    private string _currentPath = "";
    private double _currentSongProgress;
    private DateTime? _startedUtc;
    private DateTime? _completedUtc;
    private string _status = "idle";

    public LibraryAnalysisService(LibraryService library, SongAnalysisService? analysis = null)
    {
        _library = library;
        _analysis = analysis ?? SongAnalysisService.Instance;
        _analysis.Progress += OnAnalysisProgress;
    }

    public Func<Config?> ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;

    /// <summary>Lets the worker pause while the player is active.</summary>
    public Func<bool>? PlaybackActive { get; set; }

    /// <summary>Delay between songs; keeps the UI and audio thread responsive.</summary>
    public int DelayBetweenSongsMs { get; set; } = 300;

    public event Action<LibraryAnalysisProgress>? ProgressChanged;

    public LibraryAnalysisProgress GetStatus()
    {
        lock (_sync)
        {
            return new LibraryAnalysisProgress
            {
                IsRunning = _worker is { IsCompleted: false },
                IsStopping = _stopping,
                PausedForPlayback = _paused,
                Total = _total,
                Completed = _completed,
                Analyzed = _analyzed,
                Failed = _failed,
                Skipped = _skipped,
                Progress = _total > 0 ? Math.Clamp((double)_completed / _total, 0, 1) : 0,
                CurrentSongProgress = _currentSongProgress,
                CurrentSong = _currentSong,
                Status = _status,
                StartedUtc = _startedUtc,
                CompletedUtc = _completedUtc
            };
        }
    }

    /// <summary>Starts a full library pass. Already-current analyses are skipped.</summary>
    public void Start(bool force = false)
    {
        lock (_sync)
        {
            if (_worker is { IsCompleted: false }) return;
            ResetLocked();
            _force = force;
            _fullScan = true;
            _status = "running";
            _startedUtc = DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
        Publish();
    }

    /// <summary>Asks the worker to stop; the current song is allowed to finish.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_worker == null || _worker.IsCompleted) return;
            _stopping = true;
            _status = "stopping";
            try { _cts?.Cancel(); } catch { }
            while (_queue.TryDequeue(out _)) { }
            _queuedPaths.Clear();
        }
        Publish();
    }

    /// <summary>Queues one song (auto-analysis after a library import/download).</summary>
    public void QueueSong(LibrarySong song, bool force = false)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.FilePath)) return;
        var path = AppPaths.ResolvePortablePath(song.FilePath);

        lock (_sync)
        {
            if (!_queuedPaths.Add(path)) return;
            _queue.Enqueue(song);
            _total = Math.Max(_total, _completed + _queue.Count + 1);
            _force |= force;

            if (_worker is { IsCompleted: false }) return;
            ResetCountersLocked();
            _fullScan = false;
            _force = force;
            _status = "running";
            _startedUtc = DateTime.UtcNow;
            _total = _queue.Count;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
        Publish();
    }

    /// <summary>Starts a pass only when cached analyses use an older algorithm.</summary>
    public void StartIfOutdated()
    {
        if (!HasOutdatedAnalyses()) return;
        Start(force: false);
    }

    public bool HasOutdatedAnalyses()
    {
        try
        {
            return _library.Data.Songs.Any(song =>
                !string.IsNullOrWhiteSpace(song.FilePath) &&
                _analysis.IsOutdated(AppPaths.ResolvePortablePath(song.FilePath)));
        }
        catch { return false; }
    }

    /// <summary>Deletes the cached analysis when its library song is removed.</summary>
    public void RemoveSong(LibrarySong song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.FilePath)) return;
        var path = AppPaths.ResolvePortablePath(song.FilePath);
        _analysis.DeleteAnalysis(path);

        lock (_sync)
        {
            _queuedPaths.Remove(path);
            if (_completed < _total) _total = Math.Max(_completed, _total - 1);
        }
        Publish();
    }

    private async Task RunAsync(CancellationToken token)
    {
        List<LibrarySong> scanSongs;
        lock (_sync)
        {
            scanSongs = _fullScan ? SnapshotSongsNeedingAnalysis() : new List<LibrarySong>();
            _total = scanSongs.Count + _queue.Count;
            if (_total == 0) _total = scanSongs.Count;
        }
        Publish();

        int index = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (PlaybackActive?.Invoke() == true)
                {
                    lock (_sync) _paused = true;
                    Publish();
                    await Task.Delay(1200, token).ConfigureAwait(false);
                    continue;
                }
                lock (_sync) _paused = false;

                LibrarySong? song = null;
                lock (_sync)
                {
                    if (index < scanSongs.Count)
                    {
                        song = scanSongs[index++];
                    }
                    else if (_queue.TryDequeue(out var queued))
                    {
                        _queuedPaths.Remove(AppPaths.ResolvePortablePath(queued.FilePath));
                        song = queued;
                    }
                }

                if (song == null)
                {
                    if (_queue.IsEmpty) break;
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                var path = AppPaths.ResolvePortablePath(song.FilePath);
                bool stillInLibrary = _library.Data.Songs.Any(s =>
                    string.Equals(s.Id, song.Id, StringComparison.Ordinal) ||
                    string.Equals(AppPaths.ResolvePortablePath(s.FilePath), path, StringComparison.OrdinalIgnoreCase));

                if (!stillInLibrary || !System.IO.File.Exists(path))
                {
                    lock (_sync) { _skipped++; _completed++; }
                    Publish();
                    continue;
                }

                if (!_force && _analysis.GetFresh(path) != null)
                {
                    lock (_sync) { _skipped++; _completed++; }
                    Publish();
                    continue;
                }

                lock (_sync)
                {
                    _currentSong = song.Title;
                    _currentPath = path;
                    _currentSongProgress = 0;
                    _status = "running";
                }
                Publish();

                try
                {
                    await _analysis.AnalyzeAsync(path, song.Title, song.Artist, _force, token)
                        .ConfigureAwait(false);
                    lock (_sync) _analyzed++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    lock (_sync) _failed++;
                }

                lock (_sync)
                {
                    _completed++;
                    _currentSong = "";
                    _currentPath = "";
                    _currentSongProgress = 1;
                }
                Publish();

                if (DelayBetweenSongsMs > 0)
                    await Task.Delay(DelayBetweenSongsMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_sync)
            {
                bool wasStopping = _stopping;
                _paused = false;
                _stopping = false;
                _status = wasStopping ? "stopped" : "completed";
                _completedUtc = DateTime.UtcNow;
                _currentSong = "";
                _currentPath = "";
                _worker = null;
                _cts?.Dispose();
                _cts = null;
                _fullScan = false;
                _force = false;
            }
            Publish();
        }
    }

    private List<LibrarySong> SnapshotSongsNeedingAnalysis()
    {
        try
        {
            var songs = _library.Data.Songs.ToList();
            if (_force) return songs;
            return songs.Where(song =>
            {
                var path = AppPaths.ResolvePortablePath(song.FilePath);
                return System.IO.File.Exists(path) && _analysis.GetFresh(path) == null;
            }).ToList();
        }
        catch { return new List<LibrarySong>(); }
    }

    private void OnAnalysisProgress(SongAnalysisProgress progress)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(_currentPath) ||
                !string.Equals(progress.SongPath, _currentPath, StringComparison.OrdinalIgnoreCase))
                return;
            _currentSongProgress = Math.Clamp(progress.Progress, 0, 1);
        }
        Publish();
    }

    private void ResetLocked()
    {
        ResetCountersLocked();
        _completedUtc = null;
        _stopping = false;
        _paused = false;
        _currentSong = "";
        _currentPath = "";
        _currentSongProgress = 0;
    }

    private void ResetCountersLocked()
    {
        _total = 0;
        _completed = 0;
        _analyzed = 0;
        _failed = 0;
        _skipped = 0;
    }

    private void Publish()
    {
        try { ProgressChanged?.Invoke(GetStatus()); } catch { }
    }

    public void Dispose()
    {
        try { _analysis.Progress -= OnAnalysisProgress; } catch { }
        try { _cts?.Cancel(); } catch { }
    }
}
