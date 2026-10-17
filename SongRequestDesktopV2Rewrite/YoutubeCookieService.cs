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

    public static async Task<(bool ok, List<Cookie> cookies, string? error)> GrabFromBrowserAsync(string browser)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sr_cookies_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = LocateYtDlp(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(browser);
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("https://www.youtube.com");

            using var proc = Process.Start(psi);
            if (proc == null) return (false, new List<Cookie>(), "Failed to start yt-dlp.");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            var stderr = stderrTask.Result;

            if (!File.Exists(tmp))
                return (false, new List<Cookie>(),
                    $"Could not read cookies from '{browser}'. Is it installed and logged into YouTube? \n{stderr.Trim()}");

            var cookies = ParseCookies(File.ReadAllText(tmp));
            return cookies.Count > 0
                ? (true, cookies, null)
                : (false, cookies, "No cookies were found.");
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
