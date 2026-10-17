using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform YouTube cookie acquisition (replaces the Windows-only
/// CefSharp login window).
///
/// Two strategies:
///  1. Extract cookies from an installed browser via <c>yt-dlp --cookies-from-browser</c>.
///  2. Parse cookies pasted by the user (Netscape cookies.txt or a raw
///     <c>name=value; ...</c> header).
///
/// Parsed cookies are persisted to <see cref="AppPaths.YoutubeAuthPath"/> so
/// authentication survives restarts.
/// </summary>
public static class YoutubeCookieService
{
    /// <summary>Browser names understood by yt-dlp's --cookies-from-browser.</summary>
    public static readonly string[] SupportedBrowsers =
        { "chrome", "edge", "firefox", "brave", "chromium", "opera", "vivaldi", "safari" };

    /// <summary>Returns the yt-dlp path when it exists, otherwise null.</summary>
    public static string? TryFindYtDlp() => ToolLocator.Find("yt-dlp");

    public static string LocateYtDlp()
        => TryFindYtDlp() ?? (OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>
    /// How long a browser cookie import may take. Kept below the UI's 60 s request
    /// timeout so the user gets a real error instead of "Request timeout".
    /// </summary>
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Arguments for the cookie-only yt-dlp run.
    ///
    /// yt-dlp has no "just dump the cookies" mode: it writes the <c>--cookies</c> jar
    /// on exit, so it must be given a URL. Pointing it at the bare homepage makes it
    /// walk the whole *logged-in* feed (hundreds of items — it is a playlist called
    /// "recommended"), which never finishes on a real account. A flat, single-item,
    /// simulated playlist visit is one request (~1 s) and still writes the jar.
    /// </summary>
    internal static List<string> BuildYtDlpArguments(string browser, string cookieFile) => new()
    {
        "--ignore-config",           // ignore ~/.config/yt-dlp/config so the run is deterministic
        "--cookies-from-browser", browser,
        "--cookies", cookieFile,
        "--simulate",                // never download media
        "--flat-playlist",           // don't resolve individual entries
        "--playlist-items", "1",     // ...and only look at one entry
        "--no-warnings",
        "https://www.youtube.com/",
    };

    public static Task<(bool ok, List<Cookie> cookies, string? error)> GrabFromBrowserAsync(string browser)
        => GrabFromBrowserAsync(browser, LocateYtDlp(), ImportTimeout);

    /// <summary>Import with an explicit yt-dlp path/timeout (used by tests).</summary>
    internal static async Task<(bool ok, List<Cookie> cookies, string? error)> GrabFromBrowserAsync(
        string browser, string ytDlpPath, TimeSpan timeout)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sr_cookies_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in BuildYtDlpArguments(browser, tmp))
                psi.ArgumentList.Add(argument);

            using var proc = Process.Start(psi);
            if (proc == null) return (false, new List<Cookie>(), "Failed to start yt-dlp.");

            // Drain both pipes while it runs (otherwise a full buffer would block it).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            bool exited;
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    exited = true;
                }
                catch (OperationCanceledException)
                {
                    exited = false;
                }
            }

            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { await proc.WaitForExitAsync().ConfigureAwait(false); } catch { }
                return (false, new List<Cookie>(),
                    $"Reading cookies from '{browser}' took longer than {timeout.TotalSeconds:0} seconds and was stopped. "
                    + "Is the browser profile available and signed in? You can paste cookies manually instead.");
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var stderr = stderrTask.Result;

            if (!File.Exists(tmp))
                return (false, new List<Cookie>(),
                    $"Could not read cookies from '{browser}'. Is it installed and logged into YouTube? \n{Tail(stderr)}");

            var cookies = FilterToYouTube(ParseCookies(File.ReadAllText(tmp)));
            return cookies.Count > 0
                ? (true, cookies, null)
                : (false, cookies,
                    $"No YouTube cookies were found in '{browser}'. Are you signed in to YouTube there? \n{Tail(stderr)}");
        }
        catch (Exception ex)
        {
            return (false, new List<Cookie>(),
                $"yt-dlp not available or failed ({ex.Message}). You can paste cookies manually instead.");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Keeps only YouTube cookies. yt-dlp dumps the *entire* browser cookie jar
    /// (every site the user has visited), and importing that would persist unrelated
    /// credentials to the app's auth file — the YouTube session lives on
    /// <c>.youtube.com</c>, which is all the app talks to.
    /// </summary>
    internal static List<Cookie> FilterToYouTube(IEnumerable<Cookie> cookies)
    {
        var kept = new List<Cookie>();
        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain ?? string.Empty;
            if (domain.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                domain.Contains("googlevideo", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(cookie);
            }
        }
        return kept;
    }

    /// <summary>Last few non-empty lines of yt-dlp's stderr, for the error message.</summary>
    private static string Tail(string stderr, int lines = 4)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        var all = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>Parses Netscape cookies.txt or a raw "name=value; ..." header.</summary>
    public static List<Cookie> ParseCookies(string text)
    {
        var list = new List<Cookie>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var parts = line.Split('\t');
            if (parts.Length >= 7)
            {
                TryAdd(list, parts[5], parts[6], parts[0], parts[2]);
            }
            else if (line.Contains('='))
            {
                foreach (var pair in line.Split(';'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Trim().Length > 0)
                        TryAdd(list, kv[0].Trim(), kv[1].Trim(), ".youtube.com", "/");
                }
            }
        }
        return list;
    }

    private static void TryAdd(List<Cookie> list, string name, string value, string domain, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            domain = string.IsNullOrWhiteSpace(domain) ? ".youtube.com" : domain;
            if (!domain.StartsWith(".")) domain = "." + domain.TrimStart('.');
            list.Add(new Cookie(name, value, string.IsNullOrWhiteSpace(path) ? "/" : path, domain));
        }
        catch { /* skip invalid cookie */ }
    }

    // ── Persistence ────────────────────────────────────────────────

    public static IReadOnlyList<Cookie> LoadSaved(string? path = null)
    {
        var file = path ?? AppPaths.YoutubeAuthPath;
        try
        {
            if (!File.Exists(file)) return Array.Empty<Cookie>();
            var json = File.ReadAllText(file);
            var raw = JsonConvert.DeserializeObject<List<SavedCookie>>(json);
            if (raw == null) return Array.Empty<Cookie>();
            var list = new List<Cookie>();
            foreach (var c in raw)
            {
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                try { list.Add(new Cookie(c.Name, c.Value ?? "", string.IsNullOrEmpty(c.Path) ? "/" : c.Path, string.IsNullOrEmpty(c.Domain) ? ".youtube.com" : c.Domain)); }
                catch { }
            }
            return list;
        }
        catch
        {
            return Array.Empty<Cookie>();
        }
    }

    public static void Save(IReadOnlyList<Cookie> cookies, string? path = null)
    {
        var file = path ?? AppPaths.YoutubeAuthPath;
        try
        {
            var raw = cookies.Select(c => new SavedCookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Secure = c.Secure,
                HttpOnly = c.HttpOnly
            }).ToList();
            File.WriteAllText(file, JsonConvert.SerializeObject(raw, Formatting.Indented));
        }
        catch { }
    }

    public static void Clear(string? path = null)
    {
        var file = path ?? AppPaths.YoutubeAuthPath;
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    private sealed class SavedCookie
    {
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
        public string? Domain { get; set; }
        public string? Path { get; set; }
        public bool Secure { get; set; }
        public bool HttpOnly { get; set; }
    }
}
