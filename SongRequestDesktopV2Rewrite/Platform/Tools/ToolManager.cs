using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Status of a single external tool.</summary>
public sealed record ToolStatusEntry(bool Found, string? Path);

/// <summary>Status of all external tools the app can use.</summary>
public sealed class ToolStatus
{
    public ToolStatusEntry Ffmpeg { get; init; } = new(false, null);
    public ToolStatusEntry Ffprobe { get; init; } = new(false, null);
    public ToolStatusEntry YtDlp { get; init; } = new(false, null);

    /// <summary>FFmpeg (with ffprobe) is required for audio; yt-dlp is optional (cookie import).</summary>
    public bool AudioReady => Ffmpeg.Found && Ffprobe.Found;

    public bool AllPresent => AudioReady && YtDlp.Found;
}

public sealed class ToolInstallProgress
{
    public string Tool { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty; // downloading | extracting | installing | done | error
    public double Percent { get; set; }
    public string? Message { get; set; }
}

internal sealed record ToolMember(string MemberFileName, string TargetFileName, bool Executable);

internal sealed record ToolBundle(string Url, bool IsArchive, IReadOnlyList<ToolMember> Members);

/// <summary>
/// Reports the status of, and downloads, the external tools (FFmpeg/FFprobe and
/// yt-dlp) into the app's <c>tools</c> folder (or the per-user one when the app
/// folder is not writable). Downloads are per-platform static builds.
/// </summary>
public static class ToolManager
{
    public static ToolStatus GetStatus() => new()
    {
        Ffmpeg = Entry(FFmpegLocator.FindFfmpeg()),
        Ffprobe = Entry(FFmpegLocator.FindFfprobe()),
        YtDlp = Entry(YoutubeCookieService.TryFindYtDlp()),
    };

    private static ToolStatusEntry Entry(string? path) => new(!string.IsNullOrWhiteSpace(path), path);

    public static Task<bool> InstallFfmpegAsync(Action<ToolInstallProgress>? onProgress, CancellationToken ct = default)
        => InstallAsync("ffmpeg", FfmpegBundles(UpdateService.CurrentPlatform()), onProgress, ct);

    public static Task<bool> InstallYtDlpAsync(Action<ToolInstallProgress>? onProgress, CancellationToken ct = default)
        => InstallAsync("yt-dlp", YtDlpBundles(UpdateService.CurrentPlatform()), onProgress, ct);

    internal static Task<bool> InstallAsync(string tool, IReadOnlyList<ToolBundle> bundles, Action<ToolInstallProgress>? onProgress, CancellationToken ct = default)
    {
        var targetDirectory = ResolveTargetDirectory();
        return InstallCoreAsync(bundles, targetDirectory, tool, onProgress, DownloadAsync, ExtractAsync, MakeExecutable, ct);
    }

    // ── Per-platform sources ───────────────────────────────────────────────

    internal static IReadOnlyList<ToolBundle> FfmpegBundles(string platform)
    {
        if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            // gyan.dev "essentials" zip contains ffmpeg.exe + ffprobe.exe.
            return new[]
            {
                new ToolBundle(
                    "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                    true,
                    new[] { new ToolMember("ffmpeg.exe", "ffmpeg.exe", false), new ToolMember("ffprobe.exe", "ffprobe.exe", false) }),
            };
        }

        if (platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
        {
            // evermeet provides separate ffmpeg/ffprobe zips (x86_64; Rosetta on Apple silicon).
            return new[]
            {
                new ToolBundle("https://evermeet.cx/ffmpeg/getrelease/zip", true, new[] { new ToolMember("ffmpeg", "ffmpeg", true) }),
                new ToolBundle("https://evermeet.cx/ffprobe/getrelease/zip", true, new[] { new ToolMember("ffprobe", "ffprobe", true) }),
            };
        }

        // Linux: BtbN static builds, distributed as zip (no xz dependency needed).
        var flavour = platform.EndsWith("arm64", StringComparison.OrdinalIgnoreCase) ? "linuxarm64" : "linux64";
        return new[]
        {
            new ToolBundle(
                $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-{flavour}-gpl.zip",
                true,
                new[] { new ToolMember("ffmpeg", "ffmpeg", true), new ToolMember("ffprobe", "ffprobe", true) }),
        };
    }

    internal static IReadOnlyList<ToolBundle> YtDlpBundles(string platform)
    {
        if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                new ToolBundle("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", false,
                    new[] { new ToolMember("yt-dlp.exe", "yt-dlp.exe", false) }),
            };
        }

        return new[]
        {
            new ToolBundle("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp", false,
                new[] { new ToolMember("yt-dlp", "yt-dlp", true) }),
        };
    }

    // ── Install orchestration (injectable for tests) ───────────────────────

    internal static async Task<bool> InstallCoreAsync(
        IReadOnlyList<ToolBundle> bundles,
        string targetDirectory,
        string tool,
        Action<ToolInstallProgress>? onProgress,
        Func<string, string, Action<double>, CancellationToken, Task> download,
        Func<string, string, Task> extract,
        Action<string, bool> makeExecutable,
        CancellationToken ct = default)
    {
        void Report(string stage, double pct, string? message = null) =>
            onProgress?.Invoke(new ToolInstallProgress { Tool = tool, Stage = stage, Percent = pct, Message = message });

        try
        {
            Directory.CreateDirectory(targetDirectory);

            int index = 0;
            foreach (var bundle in bundles)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                Report("downloading", 0, $"Downloading {tool} ({index}/{bundles.Count})...");

                var suffix = bundle.IsArchive ? ".zip" : ".bin";
                var downloadPath = Path.Combine(Path.GetTempPath(), "sr-tool-" + Guid.NewGuid().ToString("N") + suffix);
                string? extractDirectory = null;

                try
                {
                    await download(bundle.Url, downloadPath, pct => Report("downloading", pct, $"Downloading {tool}... {pct:F0}%"), ct).ConfigureAwait(false);

                    if (bundle.IsArchive)
                    {
                        Report("extracting", 100, $"Extracting {tool}...");
                        extractDirectory = Path.Combine(Path.GetTempPath(), "sr-tool-extract-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(extractDirectory);
                        await extract(downloadPath, extractDirectory).ConfigureAwait(false);
                    }

                    Report("installing", 100, $"Installing {tool}...");
                    foreach (var member in bundle.Members)
                    {
                        var source = bundle.IsArchive ? FindFile(extractDirectory!, member.MemberFileName) : downloadPath;
                        if (source == null) continue;

                        var target = Path.Combine(targetDirectory, member.TargetFileName);
                        File.Copy(source, target, overwrite: true);
                        makeExecutable(target, member.Executable);
                    }
                }
                finally
                {
                    TryDelete(downloadPath, directory: false);
                    if (extractDirectory != null) TryDelete(extractDirectory, directory: true);
                }
            }

            ToolLocator.Invalidate();
            Report("done", 100, $"{tool} installed.");
            return true;
        }
        catch (Exception ex)
        {
            Report("error", 0, ex.Message);
            return false;
        }
    }

    internal static string ResolveTargetDirectory()
    {
        foreach (var directory in new[] { AppPaths.ToolsDirectory, AppPaths.UserToolsDirectory })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".sr-write-probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return directory;
            }
            catch { }
        }

        return AppPaths.UserToolsDirectory;
    }

    internal static string? FindFile(string directory, string fileName)
    {
        try { return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    private static async Task DownloadAsync(string url, string destination, Action<double> onProgress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        await DownloadFileAsync(url, destination, onProgress, client, ct).ConfigureAwait(false);
    }

    /// <summary>Streams a URL to a file reporting 0-100% (injectable client, used by tests).</summary>
    internal static async Task DownloadFileAsync(string url, string destination, Action<double> onProgress, HttpClient client, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;
            if (total > 0) onProgress?.Invoke((double)totalRead / total * 100);
        }
    }

    private static Task ExtractAsync(string archivePath, string destination)
        => UpdateInstaller.ExtractArchiveAsync(archivePath, destination);

    private static void MakeExecutable(string path, bool executable)
    {
        if (!executable || OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    private static void TryDelete(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }
}
