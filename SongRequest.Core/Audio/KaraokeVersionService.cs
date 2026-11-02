using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>"ai" when a local model produced the file, otherwise "spectral".</summary>
    public string Separator { get; set; } = "spectral";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public long ModelSizeBytes { get; set; }
    public int InstrumentalStemIndex { get; set; } = -1;

    /// <summary>Wall-clock time spent generating this file (AI models can be slow).</summary>
    public double GenerationSeconds { get; set; }

    /// <summary>True when AI was requested but failed and the built-in separator was used.</summary>
    public bool FallbackFromAi { get; set; }
    public string FallbackModelId { get; set; } = "";
    public string AiError { get; set; } = "";
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
/// number is stored with every entry; the built-in separator uses
/// <see cref="VocalRemover.AlgorithmVersion"/>, while AI-separated files use
/// <see cref="AiVocalRemover.VersionFor"/>. Changing the separator, model or
/// algorithm therefore makes old files stale and regenerates them when used.
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

    // Seams used by tests and hosts that provide an alternate AI backend.
    internal Func<Config?>? ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;
    internal Func<AiVocalModelStatus?>? AiModelProvider { get; set; }
    internal Func<IAiVocalSeparator?>? AiBackendProvider { get; set; } =
        () => AiVocalSeparationFactory.TryCreate();

    public KaraokeVersionService(string? indexPath = null, string? outputDirectory = null)
    {
        _indexPath = indexPath ?? AppPaths.KaraokeIndexPath;
        _outputDirectory = outputDirectory ?? AppPaths.KaraokePath;
    }

    public event Action<KaraokeGenerationProgress>? Progress;
    public event Action<KaraokeVersion>? Generated;

    /// <summary>
    /// The version that would be produced now. It includes the selected AI model
    /// id, so enabling AI, changing models, or installing a model makes existing
    /// versions stale and triggers regeneration.
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            if (TryResolveAiModel(out var aiModel))
                return AiVocalRemover.VersionFor(
                    aiModel.Model.Id, aiModel.Model.InstrumentalStemIndex);
            return VocalRemover.AlgorithmVersion;
        }
    }

    /// <summary>The built-in separator's version, regardless of AI settings.</summary>
    public static string SpectralVersion => VocalRemover.AlgorithmVersion;

    private bool TryResolveAiModel(out AiVocalModelStatus status)
    {
        status = null!;
        try
        {
            var cfg = ConfigAccessor?.Invoke();
            if (cfg == null || !cfg.EnhancedVocalRemoval) return false;
            if (!cfg.AiDisclaimerAccepted) return false;

            if (AiModelProvider != null)
            {
                var provided = AiModelProvider();
                if (provided is { Installed: true })
                {
                    status = provided;
                    return true;
                }
                return false;
            }

            if (!AiVocalSeparationFactory.IsSupported) return false;
            return AiVocalModelManager.Instance.TryGetInstalledModel(cfg.AiVocalModelId, out status);
        }
        catch { return false; }
    }

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

        string expected = CurrentVersion;
        if (!string.Equals(entry.Version, expected, StringComparison.Ordinal))
        {
            // A recorded AI fallback is accepted so a bad/unsupported model does
            // not make every play wait for another failed (and slow) AI attempt.
            // Deleting/reinstalling the model clears the fallback and retries.
            bool acceptedFallback = entry.FallbackFromAi &&
                                    string.Equals(entry.Version, VocalRemover.AlgorithmVersion, StringComparison.Ordinal) &&
                                    string.Equals(entry.FallbackModelId, SelectedAiModelId(), StringComparison.Ordinal);
            if (!acceptedFallback) return null;
        }

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

    private string SelectedAiModelId()
    {
        try { return ConfigAccessor?.Invoke()?.AiVocalModelId ?? AiVocalModelCatalog.DefaultId; }
        catch { return AiVocalModelCatalog.DefaultId; }
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
            var stopwatch = Stopwatch.StartNew();
            Action<double, string> callback = (p, stage) =>
                Report(sourcePath, title, 0.05 + p * 0.90, stage);

            AiVocalModelStatus? aiModel = null;
            bool usedAi = false;
            string? aiError = null;

            if (TryResolveAiModel(out var selectedAiModel))
            {
                var backend = AiBackendProvider?.Invoke();
                if (backend != null)
                {
                    try
                    {
                        aiModel = selectedAiModel;
                        Report(sourcePath, title, 0.04,
                            "AI vocal separation · " + aiModel.Model.Name);
                        int threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
                        backend.SeparateInstrumental(
                            sourcePath, karaokePath, aiModel.Path!,
                            aiModel.Model.InstrumentalStemIndex,
                            threads, callback, cancellationToken);
                        usedAi = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        aiError = ex.Message;
                        Report(sourcePath, title, 0.10,
                            "AI separation failed; using built-in separator: " + ex.Message);
                    }
                }
            }

            if (!usedAi)
                VocalRemover.Process(sourcePath, karaokePath, callback, cancellationToken);

            var entry = new KaraokeVersion
            {
                SourcePath = sourcePath,
                KaraokePath = karaokePath,
                Title = title,
                Artist = artist,
                Version = usedAi
                    ? AiVocalRemover.VersionFor(
                        aiModel!.Model.Id, aiModel.Model.InstrumentalStemIndex)
                    : VocalRemover.AlgorithmVersion,
                SourceSize = info.Length,
                SourceModifiedUtc = info.LastWriteTimeUtc,
                CreatedUtc = DateTime.UtcNow,
                Separator = usedAi ? "ai" : "spectral",
                ModelId = usedAi ? aiModel!.Model.Id : "",
                ModelName = usedAi ? aiModel!.Model.Name : "",
                ModelVersion = usedAi ? AiVocalRemover.AlgorithmVersion : "",
                ModelSizeBytes = usedAi ? aiModel!.Model.SizeBytes : 0,
                InstrumentalStemIndex = usedAi ? aiModel!.Model.InstrumentalStemIndex : -1,
                GenerationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                FallbackFromAi = !usedAi && aiError != null,
                FallbackModelId = !usedAi && aiError != null
                    ? SelectedAiModelId()
                    : "",
                AiError = aiError ?? ""
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

    /// <summary>
    /// Clears recorded AI fallbacks (after installing/reinstalling a model) so the
    /// next play retries the enhanced separator.
    /// </summary>
    public int ClearAiFallbacks(string? modelId = null)
    {
        EnsureLoaded();
        lock (_sync)
        {
            int cleared = 0;
            foreach (var entry in _index.Values)
            {
                if (!entry.FallbackFromAi) continue;
                if (!string.IsNullOrWhiteSpace(modelId) &&
                    !string.Equals(entry.FallbackModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry.FallbackFromAi = false;
                entry.AiError = "";
                cleared++;
            }
            if (cleared > 0) SaveLocked();
            return cleared;
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
