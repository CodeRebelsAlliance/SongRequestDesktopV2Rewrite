using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class YoutubeCookieServiceTests
{
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
