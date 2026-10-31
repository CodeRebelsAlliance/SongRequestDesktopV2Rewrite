using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Orchestrates song analysis: runs <see cref="SongAnalyzer"/>, keeps a disk
/// cache keyed by file size + modification time, and reports progress for the UI.
///
/// The cache is a single JSON document in the app data folder. Analyses of long
/// tracks take a few seconds, so callers (UI and API) share in-flight work.
/// </summary>
public sealed class SongAnalysisService
{
    public static SongAnalysisService Instance { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, SongAnalysis> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<SongAnalysis>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<TimedLyricLine>> _pendingLyrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IAudioSource> _open;
    private readonly string? _cachePath;
    private bool _loaded;

    public SongAnalysisService(Func<string, IAudioSource>? open = null, string? cachePath = null)
    {
        _open = open ?? AudioSourceFactory.Open;
        _cachePath = cachePath;
    }

    public string CachePath => _cachePath ?? AppPaths.AnalysisCachePath;

    /// <summary>Raised for every analysis progress update (UI + API WebSocket).</summary>
    public event Action<SongAnalysisProgress>? Progress;

    /// <summary>Raised when a new analysis finished (UI can refresh an open panel).</summary>
    public event Action<SongAnalysis>? Analyzed;

    public IReadOnlyList<SongAnalysis> Cached
    {
        get { EnsureLoaded(); lock (_sync) return _cache.Values.ToList(); }
    }

    public SongAnalysis? GetCached(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        EnsureLoaded();
        var key = Normalize(path);
        lock (_sync) return _cache.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Returns a cached analysis when it still matches the file, otherwise null.</summary>
    public SongAnalysis? GetFresh(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cached = GetCached(path);
        if (cached == null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (cached.FileSizeBytes == info.Length &&
                cached.FileModifiedUtc == info.LastWriteTimeUtc &&
                string.Equals(cached.AlgorithmVersion, SongAnalyzer.CurrentAlgorithmVersion, StringComparison.Ordinal))
                return cached;
        }
        catch { }
        return null;
    }

    /// <summary>True when a cached analysis exists but was produced by an older algorithm.</summary>
    public bool IsOutdated(string path)
    {
        var cached = GetCached(path);
        return cached != null &&
               !string.Equals(cached.AlgorithmVersion, SongAnalyzer.CurrentAlgorithmVersion, StringComparison.Ordinal);
    }

    /// <summary>Deletes one cached analysis (library song deletion / clear cache).</summary>
    public bool DeleteAnalysis(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        EnsureLoaded();
        var key = Normalize(path);
        lock (_sync)
        {
            if (!_cache.Remove(key)) return false;
            SaveLocked();
        }
        return true;
    }

    /// <summary>Deletes every cached analysis.</summary>
    public int ClearAll()
    {
        EnsureLoaded();
        lock (_sync)
        {
            int count = _cache.Count;
            _cache.Clear();
            SaveLocked();
            return count;
        }
    }

    public async Task<SongAnalysis> AnalyzeAsync(
        string path,
        string title = "",
        string artist = "",
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A song path is required.", nameof(path));
        var fullPath = Normalize(path);

        if (!force)
        {
            var fresh = GetFresh(fullPath);
            if (fresh != null) return fresh;
        }

        Task<SongAnalysis> task;
        lock (_sync)
        {
            if (!force && _inFlight.TryGetValue(fullPath, out var existing))
            {
                task = existing;
            }
            else
            {
                task = Task.Run(() => AnalyzeCore(fullPath, title, artist, force), cancellationToken);
                _inFlight[fullPath] = task;
                _ = task.ContinueWith(_ =>
                {
                    lock (_sync) _inFlight.Remove(fullPath);
                }, TaskScheduler.Default);
            }
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Remembers timed lyrics (non-caption) for a track. If an analysis already
    /// exists it is refined immediately; otherwise the next analysis uses them.
    /// </summary>
    public void SetTimedLyrics(string path, IReadOnlyList<TimedLyricLine> lyrics)
    {
        if (string.IsNullOrWhiteSpace(path) || lyrics == null || lyrics.Count < 4) return;
        lock (_sync)
        {
            _pendingLyrics[Normalize(path)] = lyrics;
        }
    }

    /// <summary>Re-runs only the chapter refinement step with timed lyrics.</summary>
    public bool RefineWithLyrics(string path, IReadOnlyList<TimedLyricLine> lyrics)
    {
        if (string.IsNullOrWhiteSpace(path) || lyrics == null || lyrics.Count < 4) return false;
        var key = Normalize(path);

        lock (_sync)
        {
            _pendingLyrics[key] = lyrics;
        }

        EnsureLoaded();
        SongAnalysis? analysis;
        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out analysis) && !_cache.TryGetValue(path, out analysis))
                return false;
        }

        if (analysis == null) return false;
        SongAnalyzer.RefinePartsWithLyrics(analysis, lyrics);
        lock (_sync)
        {
            _cache[key] = analysis;
            SaveLocked();
        }

        try { Analyzed?.Invoke(analysis); } catch { }
        return true;
    }

    private SongAnalysis AnalyzeCore(string path, string title, string artist, bool force)
    {
        try
        {
            var analyzer = new SongAnalyzer(_open, progress =>
            {
                try { Progress?.Invoke(progress); } catch { }
            });

            IReadOnlyList<TimedLyricLine>? timedLyrics;
            lock (_sync)
                _pendingLyrics.TryGetValue(Normalize(path), out timedLyrics);

            var analysis = analyzer.Analyze(path, title, artist, timedLyrics);
            EnsureLoaded();
            lock (_sync)
            {
                // Lyrics may have arrived while the decoder was running. Apply
                // them now so the very first playback gets the same refined
                // chapters as the cached second playback.
                if (!analysis.LyricsUsedForParts &&
                    _pendingLyrics.TryGetValue(Normalize(path), out var lateLyrics) &&
                    lateLyrics.Count >= 4)
                {
                    try { SongAnalyzer.RefinePartsWithLyrics(analysis, lateLyrics); } catch { }
                }

                _cache[Normalize(path)] = analysis;
                DeleteStaleCacheEntriesLocked();
                SaveLocked();
            }

            try { Analyzed?.Invoke(analysis); } catch { }
            return analysis;
        }
        catch
        {
            throw;
        }
    }

    /// <summary>Removes cache entries whose source file no longer exists.</summary>
    public int PruneMissing()
    {
        EnsureLoaded();
        lock (_sync)
        {
            var missing = _cache.Where(kv => !File.Exists(kv.Value.SongPath)).Select(kv => kv.Key).ToList();
            foreach (var key in missing) _cache.Remove(key);
            if (missing.Count > 0) SaveLocked();
            return missing.Count;
        }
    }

    private void DeleteStaleCacheEntriesLocked()
    {
        // Keep the cache bounded; the UI only shows current analyses and the API
        // can always re-analyze on demand.
        if (_cache.Count <= 500) return;
        foreach (var entry in _cache
                     .OrderBy(kv => kv.Value.AnalyzedUtc)
                     .Take(_cache.Count - 500)
                     .Select(kv => kv.Key)
                     .ToList())
            _cache.Remove(entry);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(CachePath)) return;
                var json = File.ReadAllText(CachePath);
                var list = JsonConvert.DeserializeObject<List<SongAnalysis>>(json);
                if (list == null) return;
                foreach (var analysis in list)
                {
                    if (analysis == null || string.IsNullOrWhiteSpace(analysis.SongPath)) continue;
                    _cache[Normalize(analysis.SongPath)] = analysis;
                }
            }
            catch
            {
                // A corrupt cache must never prevent analysis.
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var json = JsonConvert.SerializeObject(_cache.Values.ToList(), Formatting.None);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, CachePath, overwrite: true);
        }
        catch
        {
            // Analysis still succeeded; persistence is best-effort.
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>Stable short key for exposing an analysis over URLs.</summary>
    public static string KeyFor(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(NormalizeStatic(path));
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string NormalizeStatic(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
