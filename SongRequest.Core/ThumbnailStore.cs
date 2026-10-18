using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Prepares track artwork for the UI as small, cached data URLs.
///
/// The library stores whatever image it was given (YouTube artwork is often ~1 MB), and
/// the UI used to base64-inline the full-size bytes: a single music-player home load was
/// pushing ~60 MB through the IPC and making the webview unresponsive, while every queue
/// or now-playing update re-encoded whole images on the UI thread.
///
/// This store keeps a downscaled JPEG per artwork file on disk (regenerated only when the
/// source changes), caches the encoded data URL in memory, and never lets a caller wait
/// for more than a few conversions at once: anything above the per-call budget is queued
/// for a background worker and reported as "not ready yet" so the UI can placehold it and
/// ask again shortly.
/// </summary>
public sealed class ThumbnailStore
{
    /// <summary>Artwork is displayed at most ~160 px, so 320 px is plenty (even on HiDPI).</summary>
    public const int MaxWidth = 320;

    /// <summary>Sources smaller than this are reused as-is instead of running FFmpeg.</summary>
    private const int SmallEnoughBytes = 40 * 1024;

    /// <summary>Conversions allowed to run synchronously inside one call.</summary>
    private const int MaxGenerationsPerCall = 6;

    /// <summary>How many data URLs to keep in memory.</summary>
    private const int MemoryCacheLimit = 400;

    private const int ConverterTimeoutMs = 20_000;

    public static ThumbnailStore Instance { get; } = new();

    private readonly ConcurrentDictionary<string, string> _dataUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _dataUrlOrder = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly BlockingCollection<string> _workQueue = new();
    private readonly object _workerSync = new();

    private int _workersStarted;
    private int _generationsThisCall;
    private int _generationBudget = MaxGenerationsPerCall;

    /// <summary>Runs the conversion; returns true on success. Overridable for tests.</summary>
    internal Func<string, string, bool> Downscale { get; set; } = DefaultDownscale;

    /// <summary>Where the downscaled copies live.</summary>
    internal string CacheDirectory { get; set; } =
        Path.Combine(AppPaths.LocalAppDataDir, "thumbnail-cache");

    /// <summary>Whether FFmpeg can be used (false on machines without it).</summary>
    internal Func<bool> ConverterAvailable { get; set; } = () => !string.IsNullOrEmpty(FFmpegLocator.FindFfmpeg());

    /// <summary>Background conversion workers (disabled in tests for determinism).</summary>
    internal bool BackgroundWorkersEnabled { get; set; } = true;

    /// <summary>Number of artwork files still waiting for a conversion.</summary>
    public int PendingCount => _queued.Count;

    /// <summary>
    /// Data URL for an artwork file, or <c>null</c> when it is not ready yet (the caller
    /// should show a placeholder and ask again shortly).
    /// </summary>
    public string? GetDataUrl(string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return null;

        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length == 0) return null;

            var key = sourcePath + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
            if (_dataUrls.TryGetValue(key, out var cached)) return cached;

            var target = Path.Combine(CacheDirectory, Hash(key) + ".jpg");

            if (!File.Exists(target))
            {
                if (!ConverterAvailable())
                {
                    // No converter: only inline it when it is already small.
                    if (info.Length <= SmallEnoughBytes) return CacheAndReturn(key, File.ReadAllBytes(sourcePath));
                    return null;
                }

                // Stay responsive: convert a few now, queue the rest for the workers.
                if (Interlocked.Increment(ref _generationsThisCall) <= Volatile.Read(ref _generationBudget))
                {
                    if (!ConvertFile(sourcePath, target)) return null;
                }
                else
                {
                    Enqueue(key, sourcePath, target);
                    return null;
                }
            }

            return CacheAndReturn(key, File.ReadAllBytes(target));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Data URL for artwork that only exists in memory (embedded album art). The bytes are
    /// written to the cache directory once so FFmpeg can downscale them.
    /// </summary>
    public string? GetDataUrl(byte[]? bytes, string cacheKey)
    {
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            var key = cacheKey + "|" + bytes.Length + "|" + Hash(Convert.ToBase64String(bytes));
            if (_dataUrls.TryGetValue(key, out var cached)) return cached;

            var source = Path.Combine(CacheDirectory, Hash(key) + ".src");
            var target = Path.Combine(CacheDirectory, Hash(key) + ".jpg");

            if (!File.Exists(target))
            {
                if (!ConverterAvailable() || bytes.Length <= SmallEnoughBytes)
                {
                    if (bytes.Length > SmallEnoughBytes && !ConverterAvailable()) return null;
                    Directory.CreateDirectory(CacheDirectory);
                    return CacheAndReturn(key, bytes);
                }

                Directory.CreateDirectory(CacheDirectory);
                if (!File.Exists(source)) File.WriteAllBytes(source, bytes);

                if (Interlocked.Increment(ref _generationsThisCall) <= Volatile.Read(ref _generationBudget))
                {
                    if (!ConvertFile(source, target)) return null;
                }
                else
                {
                    Enqueue(key, source, target);
                    return null;
                }
            }

            return CacheAndReturn(key, File.ReadAllBytes(target));
        }
        catch
        {
            return null;
        }
    }



    /// <summary>
    /// Starts a call and sets how many conversions it may run inline.
    /// Feed-style calls pass 0: whatever is already prepared is included, everything
    /// else is queued for the background workers and reported as "not ready yet".
    /// </summary>
    public void BeginRequest(int generationsAllowed = MaxGenerationsPerCall)
    {
        Volatile.Write(ref _generationBudget, Math.Max(0, generationsAllowed));
        Interlocked.Exchange(ref _generationsThisCall, 0);
    }

    /// <summary>Warms the cache for a set of artwork files in the background.</summary>
    public void Prewarm(IEnumerable<string> sourcePaths)
    {
        if (sourcePaths == null) return;
        foreach (var path in sourcePaths.Take(2000))
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) continue;

                var key = path + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
                var target = Path.Combine(CacheDirectory, Hash(key) + ".jpg");
                if (!File.Exists(target)) Enqueue(key, path, target);
            }
            catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Internals
    // ──────────────────────────────────────────────────────────────

    private void Enqueue(string key, string source, string target)
    {
        if (!_queued.TryAdd(key, 0)) return;
        EnsureWorkers();
        try { _workQueue.Add(key + "\n" + source + "\n" + target); } catch { }
    }

    private void EnsureWorkers()
    {
        if (!BackgroundWorkersEnabled) return;
        if (Volatile.Read(ref _workersStarted) == 1) return;
        lock (_workerSync)
        {
            if (_workersStarted == 1) return;
            _workersStarted = 1;
            for (int i = 0; i < 2; i++)
            {
                var thread = new Thread(WorkerLoop) { IsBackground = true, Name = "thumbnail-worker" };
                thread.Start();
            }
        }
    }

    private void WorkerLoop()
    {
        foreach (var item in _workQueue.GetConsumingEnumerable())
        {
            try
            {
                var parts = item.Split('\n');
                if (parts.Length != 3) continue;
                var key = parts[0];
                if (!File.Exists(parts[2])) ConvertFile(parts[1], parts[2]);
                _queued.TryRemove(key, out _);
            }
            catch { }
        }
    }

    private bool ConvertFile(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            // Already cached by another caller/worker meanwhile?
            if (File.Exists(target)) return true;

            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            if (!Downscale(source, temporary)) return false;

            try { File.Move(temporary, target, overwrite: true); }
            catch
            {
                try { File.Delete(temporary); } catch { }
                return File.Exists(target);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? CacheAndReturn(string key, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;

        var url = "data:image/jpeg;base64," + System.Convert.ToBase64String(bytes);
        if (_dataUrls.TryAdd(key, url))
        {
            _dataUrlOrder.Enqueue(key);
            while (_dataUrlOrder.Count > MemoryCacheLimit && _dataUrlOrder.TryDequeue(out var oldest))
                _dataUrls.TryRemove(oldest, out _);
        }
        return url;
    }

    private static string Hash(string value)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    /// <summary>FFmpeg conversion: downscale to <see cref="MaxWidth"/> and re-encode as JPEG.</summary>
    private static bool DefaultDownscale(string source, string target)
    {
        try
        {
            var ffmpeg = FFmpegLocator.FindFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            // -f image2: the temporary file has no extension, so FFmpeg cannot infer
            // the container from the name (it fails with "suitable output format").
            var args = "-y -hide_banner -loglevel error -i \"" + source + "\" " +
                       "-vf \"scale='min(" + MaxWidth + ",iw)':-2\" -q:v 6 -frames:v 1 -f image2 \"" + target + "\"";

            using var process = Process.Start(new ProcessStartInfo(ffmpeg, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return false;

            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(ConverterTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
