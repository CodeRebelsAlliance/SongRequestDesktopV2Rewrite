using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class YoutubeCookieServiceTests
{
    // ── yt-dlp invocation (the cookie-only export) ─────────────────────────

    [Fact]
    public void BuildYtDlpArguments_ExportsCookiesWithoutWalkingTheFeed()
    {
        var args = YoutubeCookieService.BuildYtDlpArguments("firefox", "/tmp/cookies.txt");

        // Must read from the browser and write the jar.
        Assert.Contains("--cookies-from-browser", args);
        Assert.Equal("firefox", args[args.IndexOf("--cookies-from-browser") + 1]);
        Assert.Contains("--cookies", args);
        Assert.Equal("/tmp/cookies.txt", args[args.IndexOf("--cookies") + 1]);

        // Must stay a single simulated, flat entry: pointing yt-dlp at the bare
        // homepage walks the whole logged-in "recommended" playlist (hundreds of
        // items) and never finishes, so the jar is never written.
        Assert.Contains("--simulate", args);
        Assert.Contains("--flat-playlist", args);
        Assert.Equal("1", args[args.IndexOf("--playlist-items") + 1]);

        // Deterministic: ignore the user's yt-dlp config.
        Assert.Contains("--ignore-config", args);

        Assert.Contains("https://www.youtube.com/", args);
        Assert.DoesNotContain("--skip-download", args);
    }

    // ── domain filtering ──────────────────────────────────────────────────

    [Fact]
    public void FilterToYouTube_KeepsOnlyYouTubeCookies()
    {
        var cookies = new List<Cookie>
        {
            new("SID", "v1", "/", ".youtube.com"),
            new("SAPISID", "v2", "/", "youtube.com"),
            new("__Secure-1PSID", "v3", "/", ".google.com"),
            new("session-id", "v4", "/", ".amazon.de"),
            new("cf_clearance", "v5", "/", ".fandom.com"),
            new("nid", "v6", "/", ".googlevideo.com"),
        };

        var kept = YoutubeCookieService.FilterToYouTube(cookies);

        Assert.Equal(new[] { "SID", "SAPISID", "nid" }, kept.Select(c => c.Name));
    }

    [Fact]
    public void FilterToYouTube_EmptyInput_IsEmpty()
        => Assert.Empty(YoutubeCookieService.FilterToYouTube(new List<Cookie>()));

    [Fact]
    public void ImportTimeout_IsBelowTheUiRequestTimeout()
        => Assert.True(YoutubeCookieService.ImportTimeout < TimeSpan.FromSeconds(60),
            "the import must fail before the UI's 60s request timeout, so the user sees a real error");

    [Fact]
    public void ParseCookies_NetscapeFormat()
    {
        const string text =
            "# Netscape HTTP Cookie File\n" +
            ".youtube.com\tTRUE\t/\tTRUE\t1234567890\tPREF\tvalue123\n" +
            ".youtube.com\tTRUE\t/\tFALSE\t0\tSID\tabc";

        var cookies = YoutubeCookieService.ParseCookies(text);

        Assert.Equal(2, cookies.Count);
        Assert.Contains(cookies, c => c.Name == "PREF" && c.Value == "value123");
        Assert.Contains(cookies, c => c.Name == "SID" && c.Value == "abc");
    }

    [Fact]
    public void ParseCookies_RawHeader()
    {
        var cookies = YoutubeCookieService.ParseCookies("a=1; b=2; c=3");

        Assert.Equal(3, cookies.Count);
        Assert.Contains(cookies, c => c.Name == "a" && c.Value == "1");
        Assert.Contains(cookies, c => c.Name == "c" && c.Value == "3");
    }

    [Fact]
    public void ParseCookies_BlankOrComment_ReturnsEmpty()
    {
        Assert.Empty(YoutubeCookieService.ParseCookies(""));
        Assert.Empty(YoutubeCookieService.ParseCookies("   \n  \n"));
        Assert.Empty(YoutubeCookieService.ParseCookies("# only a comment"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "auth.json");
        var cookies = new List<Cookie>
        {
            new Cookie("PREF", "v", "/", ".youtube.com"),
            new Cookie("SID", "s", "/", ".youtube.com"),
        };

        YoutubeCookieService.Save(cookies, path);
        var loaded = YoutubeCookieService.LoadSaved(path);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, c => c.Name == "PREF" && c.Value == "v");
        Assert.Contains(loaded, c => c.Name == "SID" && c.Value == "s");
    }

    [Fact]
    public void Clear_RemovesPersistedFile()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "auth.json");
        YoutubeCookieService.Save(new List<Cookie> { new Cookie("a", "1", "/", ".youtube.com") }, path);
        Assert.True(File.Exists(path));

        YoutubeCookieService.Clear(path);

        Assert.False(File.Exists(path));
        Assert.Empty(YoutubeCookieService.LoadSaved(path));
    }

    [Fact]
    public void LoadSaved_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "does-not-exist.json");
        Assert.Empty(YoutubeCookieService.LoadSaved(path));
    }
}
