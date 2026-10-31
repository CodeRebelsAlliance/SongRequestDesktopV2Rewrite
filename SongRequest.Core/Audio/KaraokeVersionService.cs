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

/// <summary>One generated karaoke version linked to its original song.</summary>
public sealed class KaraokeVersion
{
    public string SourcePath { get; set; } = "";
    public string KaraokePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Version { get; set; } = "";
    public long SourceSize { get; set; }
    public DateTime SourceModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int SampleRate { get; set; }
}

/// <summary>Progress event for the karaoke-generation UI.</summary>
public sealed class KaraokeGenerationProgress
{
    public string SourcePath { get; init; } = "";
    public string Title { get; init; } = "";
    public double Progress { get; init; }
    public string Stage { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// Generates, versions and links on-device karaoke/instrumental files. A version
/// number is stored with every entry; bumping <see cref="VocalRemover.AlgorithmVersion"/>
/// makes old files stale and regenerates them when used.
/// </summary>
public sealed class KaraokeVersionService
{
    public static KaraokeVersionService Instance { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, KaraokeVersion> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<KaraokeVersion?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _indexPath;
    private readonly string _outputDirectory;
    private bool _loaded;

    public KaraokeVersionService(string? indexPath = null, string? outputDirectory = null)
    {
        _indexPath = indexPath ?? AppPaths.KaraokeIndexPath;
        _outputDirectory = outputDirectory ?? AppPaths.KaraokePath;
    }

    public event Action<KaraokeGenerationProgress>? Progress;
    public event Action<KaraokeVersion>? Generated;

    public static string CurrentVersion => VocalRemover.AlgorithmVersion;

    public string OutputDirectory => _outputDirectory;

    public IReadOnlyList<KaraokeVersion> All
    {
        get { EnsureLoaded(); lock (_sync) return _index.Values.ToList(); }
    }

    /// <summary>Returns the linked version regardless of age, when the file exists.</summary>
    public KaraokeVersion? Get(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        EnsureLoaded();
        lock (_sync)
        {
            if (!_index.TryGetValue(Normalize(sourcePath), out var entry)) return null;
            return File.Exists(entry.KaraokePath) ? entry : null;
        }
    }

    /// <summary>Returns a usable version only when algorithm and source identity match.</summary>
    public KaraokeVersion? GetCurrent(string sourcePath)
    {
        var entry = Get(sourcePath);
        if (entry == null) return null;
        if (!string.Equals(entry.Version, CurrentVersion, StringComparison.Ordinal)) return null;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) return null;
            if (entry.SourceSize != info.Length ||
                Math.Abs((entry.SourceModifiedUtc - info.LastWriteTimeUtc).TotalSeconds) > 0.01)
                return null;
        }
        catch { return null; }
        return entry;
    }

    public bool NeedsGeneration(string sourcePath) => GetCurrent(sourcePath) == null;

    /// <summary>
    /// Ensures a current karaoke file exists. Concurrent requests for the same
    /// song share one generation task. Returns null when generation failed.
    /// </summary>
    public Task<KaraokeVersion?> EnsureAsync(
        string sourcePath,
        string title = "",
        string artist = "",
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return Task.FromResult<KaraokeVersion?>(null);

        var fullPath = Normalize(sourcePath);
        if (!force)
        {
            var current = GetCurrent(fullPath);
            if (current != null) return Task.FromResult<KaraokeVersion?>(current);
        }

        Task<KaraokeVersion?> task;
        lock (_sync)
        {
            if (_inFlight.TryGetValue(fullPath, out var existing))
                return existing;

            task = Task.Run(() => GenerateCore(fullPath, title, artist, force, cancellationToken), cancellationToken);
            _inFlight[fullPath] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_sync) _inFlight.Remove(fullPath);
            }, TaskScheduler.Default);
        }
        return task;
    }

    private KaraokeVersion? GenerateCore(
        string sourcePath,
        string title,
        string artist,
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) throw new FileNotFoundException("Song file not found.", sourcePath);

            if (!force)
            {
                var current = GetCurrent(sourcePath);
                if (current != null) return current;
            }

            Directory.CreateDirectory(_outputDirectory);
            string karaokePath = Path.Combine(_outputDirectory, FileNameFor(sourcePath));
            Report(sourcePath, title, 0.02, "Preparing karaoke version");
            Action<double, string> callback = (p, stage) =>
                Report(sourcePath, title, 0.05 + p * 0.90, "Separating vocals");

            VocalRemover.Process(sourcePath, karaokePath, callback, cancellationToken);

            var entry = new KaraokeVersion
            {
                SourcePath = sourcePath,
                KaraokePath = karaokePath,
                Title = title,
                Artist = artist,
                Version = CurrentVersion,
                SourceSize = info.Length,
                SourceModifiedUtc = info.LastWriteTimeUtc,
                CreatedUtc = DateTime.UtcNow
            };

            EnsureLoaded();
            lock (_sync)
            {
                _index[Normalize(sourcePath)] = entry;
                SaveLocked();
            }

            Report(sourcePath, title, 1.0, "Karaoke version ready");
            try { Generated?.Invoke(entry); } catch { }
            return entry;
        }
        catch (OperationCanceledException)
        {
            Report(sourcePath, title, 0, "Karaoke generation cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Report(sourcePath, title, 0, "Karaoke generation failed: " + ex.Message, ex.Message);
            return null;
        }
    }

    public bool Delete(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        EnsureLoaded();
        var key = Normalize(sourcePath);
        KaraokeVersion? entry;
        lock (_sync)
        {
            if (!_index.Remove(key, out entry)) return false;
            SaveLocked();
        }

        try { if (File.Exists(entry.KaraokePath)) File.Delete(entry.KaraokePath); } catch { }
        return true;
    }

    public int PruneMissing()
    {
        EnsureLoaded();
        lock (_sync)
        {
            var missing = _index
                .Where(kv => !File.Exists(kv.Value.KaraokePath) || !File.Exists(kv.Value.SourcePath))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in missing) _index.Remove(key);
            if (missing.Count > 0) SaveLocked();
            return missing.Count;
        }
    }

    private void Report(string sourcePath, string title, double progress, string stage, string error = "")
    {
        try
        {
            Progress?.Invoke(new KaraokeGenerationProgress
            {
                SourcePath = sourcePath,
                Title = title,
                Progress = Math.Clamp(progress, 0, 1),
                Stage = stage,
                Error = error
            });
        }
        catch { }
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
                if (!File.Exists(_indexPath)) return;
                var json = File.ReadAllText(_indexPath);
                var list = JsonConvert.DeserializeObject<List<KaraokeVersion>>(json);
                if (list == null) return;
                foreach (var entry in list)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.SourcePath)) continue;
                    _index[Normalize(entry.SourcePath)] = entry;
                }
            }
            catch { }
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(_index.Values.ToList(), Formatting.Indented);
            var temp = _indexPath + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, _indexPath, overwrite: true);
        }
        catch { }
    }

    private string FileNameFor(string sourcePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sourcePath)));
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (char invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '_');
        if (stem.Length > 48) stem = stem[..48];
        return $"{stem}-karaoke-{Convert.ToHexString(hash, 0, 6).ToLowerInvariant()}.wav";
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
