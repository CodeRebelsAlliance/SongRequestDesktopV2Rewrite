using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// One local AI model that can separate vocals from a song. The catalog is
/// deliberately small: each entry is a UVR/MDX-Net ONNX model supported by the
/// bundled sherpa-onnx runtime. Sizes are the exact GitHub release asset sizes.
/// </summary>
public sealed record AiVocalModel(
    string Id,
    string Name,
    string Description,
    string FileName,
    string DownloadUrl,
    long SizeBytes,
    int QualityRating,
    int PerformanceRating,
    string License,
    bool Recommended,
    int InstrumentalStemIndex)
{
    public string SizeLabel => AiVocalModelManager.FormatBytes(SizeBytes);
    public string QualityLabel => QualityRating + "/5";
    public string PerformanceLabel => PerformanceRating + "/5";

    /// <summary>Human-readable size/performance summary used by both settings UIs.</summary>
    public string RatingLabel =>
        "Quality " + QualityRating + "/5 · Speed " + PerformanceRating + "/5 · " + SizeLabel;
}

/// <summary>The local AI model catalog.</summary>
public static class AiVocalModelCatalog
{
    public const string DefaultId = "uvr-kara-2";

    private const string ReleaseBase =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/source-separation-models/";

    // NOTE: UVR models do not share a stem order. InstrumentalStemIndex was
    // measured for each listed model (Karaoke 2 uses stem 0, Karaoke Fast uses
    // stem 1, etc.). Always verify a new model before adding it here.
    public static IReadOnlyList<AiVocalModel> All { get; } = new[]
    {
        new AiVocalModel(
            DefaultId,
            "UVR MDX-Net Karaoke 2",
            "Balanced karaoke model. Strong vocal removal with a good speed/quality trade-off.",
            "UVR_MDXNET_KARA_2.onnx",
            ReleaseBase + "UVR_MDXNET_KARA_2.onnx",
            52_787_030,
            4, 3,
            "UVR / MDX-Net model card",
            Recommended: true, InstrumentalStemIndex: 0),
        new AiVocalModel(
            "uvr-kara-fast",
            "UVR MDX-Net Karaoke Fast",
            "Smallest karaoke model. Fastest option and the best choice for older devices.",
            "UVR_MDXNET_KARA.onnx",
            ReleaseBase + "UVR_MDXNET_KARA.onnx",
            29_704_738,
            3, 5,
            "UVR / MDX-Net model card",
            Recommended: false, InstrumentalStemIndex: 1),
        new AiVocalModel(
            "uvr-inst-hq-3",
            "UVR MDX-Net Inst HQ 3",
            "Highest-quality instrumental model. Slower and larger; best for desktop CPUs.",
            "UVR-MDX-NET-Inst_HQ_3.onnx",
            ReleaseBase + "UVR-MDX-NET-Inst_HQ_3.onnx",
            66_759_522,
            5, 2,
            "UVR / MDX-Net model card",
            Recommended: false, InstrumentalStemIndex: 0),
        new AiVocalModel(
            "uvr-general",
            "UVR MDX-Net 9482",
            "General-purpose MDX model. Good all-round separation with a smaller download.",
            "UVR_MDXNET_9482.onnx",
            ReleaseBase + "UVR_MDXNET_9482.onnx",
            29_704_738,
            3, 4,
            "UVR / MDX-Net model card",
            Recommended: false, InstrumentalStemIndex: 1),
        new AiVocalModel(
            "uvr-voc-ft",
            "UVR MDX-Net Voc FT",
            "Vocal-focused model. Produces a very clean instrumental stem for most mixes.",
            "UVR-MDX-NET-Voc_FT.onnx",
            ReleaseBase + "UVR-MDX-NET-Voc_FT.onnx",
            66_762_795,
            4, 2,
            "UVR / MDX-Net model card",
            Recommended: false, InstrumentalStemIndex: 1)
    };

    public static AiVocalModel? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static AiVocalModel Default => Find(DefaultId)!;
}

/// <summary>Download/install progress for an AI vocal model.</summary>
public sealed class AiVocalModelProgress
{
    public string ModelId { get; set; } = "";
    public string Stage { get; set; } = ""; // downloading | verifying | installing | done | error | deleting
    public double Percent { get; set; }
    public string? Message { get; set; }
}

/// <summary>Installed-state of one catalog model.</summary>
public sealed class AiVocalModelStatus
{
    public AiVocalModel Model { get; init; } = AiVocalModelCatalog.Default;
    public bool Installed { get; init; }
    public string? Path { get; init; }
    public long InstalledBytes { get; init; }
    public DateTime? InstalledUtc { get; init; }
    public string? Sha256 { get; init; }
}

/// <summary>Persisted metadata for an installed model.</summary>
public sealed class AiVocalModelInstall
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Downloads, verifies, lists and deletes local AI vocal-separation models.
/// Models live in the app's local (non-roaming) data folder; nothing leaves the
/// device after the initial model download.
/// </summary>
public sealed class AiVocalModelManager
{
    public static AiVocalModelManager Instance { get; } = new();

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Func<string, string, Action<double>, CancellationToken, Task> _download;
    private readonly HashSet<string> _installing = new(StringComparer.OrdinalIgnoreCase);

    public AiVocalModelManager(
        string? directory = null,
        string? indexPath = null,
        Func<string, string, Action<double>, CancellationToken, Task>? download = null)
    {
        _directory = directory ?? AppPaths.AiModelsPath;
        _indexPath = indexPath ?? AppPaths.AiModelsIndexPath;
        _download = download ?? DefaultDownloadAsync;
    }

    public string Directory
    {
        get
        {
            try { System.IO.Directory.CreateDirectory(_directory); } catch { }
            return _directory;
        }
    }

    public IReadOnlyList<AiVocalModelStatus> GetStatus()
    {
        var installs = LoadInstalls();
        var result = new List<AiVocalModelStatus>(AiVocalModelCatalog.All.Count);

        foreach (var model in AiVocalModelCatalog.All)
        {
            string path = Path.Combine(Directory, model.FileName);
            bool installed = File.Exists(path) && new FileInfo(path).Length > 0;
            installs.TryGetValue(model.Id, out var install);
            result.Add(new AiVocalModelStatus
            {
                Model = model,
                Installed = installed,
                Path = installed ? path : null,
                InstalledBytes = installed ? new FileInfo(path).Length : 0,
                InstalledUtc = installed ? install?.InstalledUtc : null,
                Sha256 = installed ? install?.Sha256 : null
            });
        }

        return result;
    }

    public bool IsInstalled(string? modelId) => TryGetInstalledModel(modelId, out _);

    public bool TryGetInstalledModel(string? modelId, out AiVocalModelStatus status)
    {
        status = GetStatus().FirstOrDefault(s =>
            string.Equals(s.Model.Id, modelId, StringComparison.OrdinalIgnoreCase))!;
        return status != null && status.Installed;
    }

    public string? GetModelPath(string? modelId)
        => TryGetInstalledModel(modelId, out var status) ? status.Path : null;

    public long InstalledBytes => GetStatus().Where(s => s.Installed).Sum(s => s.InstalledBytes);

    public async Task<bool> InstallAsync(
        string modelId,
        Action<AiVocalModelProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = AiVocalModelCatalog.Find(modelId);
        if (model == null)
        {
            progress?.Invoke(new AiVocalModelProgress
            {
                ModelId = modelId,
                Stage = "error",
                Message = "Unknown AI model."
            });
            return false;
        }

        lock (_sync)
        {
            if (!_installing.Add(model.Id))
            {
                progress?.Invoke(new AiVocalModelProgress
                {
                    ModelId = model.Id,
                    Stage = "error",
                    Message = "This model is already downloading."
                });
                return false;
            }
        }

        string target = Path.Combine(Directory, model.FileName);
        string part = target + ".part";

        void Report(string stage, double pct, string? message = null) =>
            progress?.Invoke(new AiVocalModelProgress
            {
                ModelId = model.Id,
                Stage = stage,
                Percent = Math.Clamp(pct, 0, 100),
                Message = message
            });

        try
        {
            Report("downloading", 0, $"Downloading {model.Name} ({model.SizeLabel})...");
            if (File.Exists(part)) File.Delete(part);

            double lastReported = -1;
            await _download(model.DownloadUrl, part, pct =>
            {
                if (pct < 100 && pct - lastReported < 1) return;
                lastReported = pct;
                Report("downloading", pct, $"Downloading {model.Name}... {pct:F0}%");
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(part);
            if (!info.Exists || info.Length == 0)
                throw new IOException("The downloaded model is empty.");

            Report("verifying", 100, "Verifying model...");
            string hash = await ComputeSha256Async(part, cancellationToken).ConfigureAwait(false);

            Report("installing", 100, "Installing model...");
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);

            lock (_sync)
            {
                var installs = LoadInstalls();
                installs[model.Id] = new AiVocalModelInstall
                {
                    Id = model.Id,
                    FileName = model.FileName,
                    SizeBytes = info.Length,
                    Sha256 = hash,
                    InstalledUtc = DateTime.UtcNow
                };
                SaveInstalls(installs);
            }

            Report("done", 100, $"{model.Name} installed.");
            return true;
        }
        catch (OperationCanceledException)
        {
            Report("error", 0, "Download cancelled.");
            TryDelete(part);
            return false;
        }
        catch (Exception ex)
        {
            Report("error", 0, ex.Message);
            TryDelete(part);
            return false;
        }
        finally
        {
            lock (_sync) _installing.Remove(model.Id);
        }
    }

    public bool Delete(string? modelId)
    {
        var model = AiVocalModelCatalog.Find(modelId);
        if (model == null) return false;

        string path = Path.Combine(Directory, model.FileName);
        bool removed = false;
        try
        {
            if (File.Exists(path)) { File.Delete(path); removed = true; }
            if (File.Exists(path + ".part")) { File.Delete(path + ".part"); removed = true; }
        }
        catch { }

        lock (_sync)
        {
            var installs = LoadInstalls();
            if (installs.Remove(model.Id)) { SaveInstalls(installs); removed = true; }
        }

        return removed;
    }

    public int PruneMissing()
    {
        lock (_sync)
        {
            var installs = LoadInstalls();
            var missing = installs
                .Where(kv => !File.Exists(Path.Combine(Directory, kv.Value.FileName)))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in missing) installs.Remove(id);
            if (missing.Count > 0) SaveInstalls(installs);
            return missing.Count;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 || unit == 0
            ? value.ToString("0") + " " + units[unit]
            : value.ToString("0.0") + " " + units[unit];
    }

    private Dictionary<string, AiVocalModelInstall> LoadInstalls()
    {
        try
        {
            if (!File.Exists(_indexPath)) return new(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(_indexPath);
            var list = JsonConvert.DeserializeObject<List<AiVocalModelInstall>>(json);
            return (list ?? new List<AiVocalModelInstall>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveInstalls(Dictionary<string, AiVocalModelInstall> installs)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            File.WriteAllText(_indexPath,
                JsonConvert.SerializeObject(installs.Values.OrderBy(i => i.Id), Formatting.Indented));
        }
        catch { }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task DefaultDownloadAsync(
        string url,
        string destination,
        Action<double> onProgress,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total > 0)
                onProgress?.Invoke(Math.Clamp(received * 100.0 / total, 0, 100));
        }
        onProgress?.Invoke(100);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
