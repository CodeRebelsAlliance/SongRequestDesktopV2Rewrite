using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// yt-dlp-based download fallback.
///
/// YoutubeExplode talks to YouTube's web client directly, and YouTube now serves
/// some music videos ("Video is not available") to clients that cannot solve its
/// current player challenges — cookies alone do not change that. yt-dlp (already
/// bundled for cookie import) does solve them, so when the direct download reports
/// a video as unavailable the app asks yt-dlp for the audio instead.
///
/// It is deliberately a *fallback*: the fast, dependency-free path stays the
/// default, and hosts without yt-dlp (mobile, or a desktop without the tool) keep
/// the original error.
/// </summary>
internal static class YtDlpDownloader
{
    /// <summary>Whether the fallback can run here (yt-dlp is present).</summary>
    public static bool IsAvailable => !string.IsNullOrEmpty(YoutubeCookieService.TryFindYtDlp());

    /// <summary>
    /// yt-dlp format selector. Desktop takes the absolute best audio (usually Opus);
    /// mobile asks for its preferred decodable container first. The old
    /// unconditional m4a preference always took the lower-bitrate AAC stream on
    /// desktop.
    /// </summary>
    internal static string FormatSelector()
        => string.IsNullOrEmpty(DownloadedAudio.PreferredContainer)
            ? "bestaudio/best"
            : $"bestaudio[ext={DownloadedAudio.PreferredExtension.TrimStart('.')}]/bestaudio/best";

    /// <summary>
    /// Downloads the audio for <paramref name="videoId"/> into <paramref name="targetPath"/>.
    /// Returns null on success, or an error message.
    /// </summary>
    public static async Task<string?> DownloadAudioAsync(
        string videoId,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        var ytDlp = YoutubeCookieService.TryFindYtDlp();
        if (string.IsNullOrEmpty(ytDlp)) return "yt-dlp is not available.";

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var cookieFile = Path.Combine(Path.GetTempPath(), "sr_dl_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // Reuse the app's own cookies when there are any: age-restricted videos
            // stay unavailable without them.
            try
            {
                var cookies = YoutubeCookieService.LoadSaved();
                if (cookies.Count > 0) YoutubeCookieService.SaveNetscapeFile(cookies, cookieFile);
            }
            catch { }

            // Temp output template: yt-dlp appends the real container extension, and
            // the app wants the exact path it chose (mobile's .m4a included).
            var template = targetPath + ".part.%(ext)s";

            var psi = new ProcessStartInfo
            {
                FileName = ytDlp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--newline");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(FormatSelector());
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(template);
            if (File.Exists(cookieFile))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookieFile);
            }
            psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

            using var process = Process.Start(psi);
            if (process == null) return "yt-dlp could not be started.";

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();

            var lastReported = -1.0;
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                if (TryParseProgress(line, out var percent) && Math.Abs(percent - lastReported) >= 1)
                {
                    lastReported = percent;
                    progress?.Report(percent / 100.0);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = Tail(stderr.ToString());
                return string.IsNullOrEmpty(detail) ? "yt-dlp failed." : detail;
            }

            return MoveResult(targetPath);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { if (File.Exists(cookieFile)) File.Delete(cookieFile); } catch { }
        }
    }

    /// <summary>Moves yt-dlp's <c>.part.&lt;ext&gt;</c> result to the exact target path.</summary>
    private static string? MoveResult(string targetPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(targetPath) ?? ".";
            var prefix = Path.GetFileName(targetPath) + ".part.";
            var produced = Directory.GetFiles(directory, Path.GetFileName(targetPath) + ".part.*");

            if (produced.Length == 0)
                return File.Exists(targetPath) ? null : "yt-dlp produced no file.";

            var source = produced[0];
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(source, targetPath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Parses yt-dlp's <c>[download]  42.3% of ...</c> progress lines.</summary>
    internal static bool TryParseProgress(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line)) return false;
        if (!line.StartsWith("[download]", StringComparison.Ordinal)) return false;

        var index = line.IndexOf('%');
        if (index <= 0) return false;

        // Walk back to the start of the number ("  42.3%").
        var start = index - 1;
        while (start >= 0 && (char.IsDigit(line[start]) || line[start] == '.' || line[start] == ',')) start--;
        start++;

        var text = line[start..index].Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }

    private static string Tail(string stderr, int lines = 4)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;

        var all = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var start = Math.Max(0, all.Length - lines);
        return string.Join("\n", all[start..]).Trim();
    }
}
