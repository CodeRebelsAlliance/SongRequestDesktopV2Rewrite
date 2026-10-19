using System.Collections.Concurrent;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The auth/fallback fixes from the post-porting regression hunt: the bearer token
/// is normalised before it becomes an Authorization header, cookies can be written
/// as the Netscape file yt-dlp reads, and yt-dlp progress lines are parsed.
/// </summary>
public class AuthAndFallbackTests
{
    private static (YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (interop, responses);
    }

    private static async Task<JObject> Request(
        YoutubeFormInterop interop,
        ConcurrentQueue<string> responses,
        string method,
        JObject payload,
        string id = "a1")
    {
        payload["type"] = "request";
        payload["id"] = id;
        payload["method"] = method;
        interop.HandleMessage(payload.ToString(Formatting.None));

        for (int i = 0; i < 300; i++)
        {
            while (responses.TryDequeue(out var json))
            {
                var obj = JObject.Parse(json);
                if (obj["type"]?.ToString() == "response" && obj["id"]?.ToString() == id)
                    return obj;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException($"No response for '{method}'");
    }

    private static async Task SaveToken(YoutubeFormInterop interop, ConcurrentQueue<string> responses, string token)
    {
        var settings = new JObject { ["bearerToken"] = token };
        await Request(interop, responses, "saveSettings", new JObject { ["settings"] = settings }, id: "t" + token.Length);
    }

    [Fact]
    public async Task SaveSettings_NormalisesABareBearerToken()
    {
        var saved = ConfigService.Instance.Current!.BearerToken;
        try
        {
            var (interop, responses) = Create();

            // A raw JWT pasted on a phone (no scheme): the header must still be valid.
            await SaveToken(interop, responses, "  abc.def.ghi  ");
            Assert.Equal("Bearer abc.def.ghi", ConfigService.Instance.Current!.BearerToken);

            // Already prefixed: unchanged (apart from whitespace).
            await SaveToken(interop, responses, "Bearer xyz");
            Assert.Equal("Bearer xyz", ConfigService.Instance.Current!.BearerToken);
        }
        finally
        {
            ConfigService.Instance.Update(cfg => cfg.BearerToken = saved);
        }
    }

    [Fact]
    public void SaveNetscapeFile_WritesWhatYtDlpReads()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "cookies.txt");
        var cookies = new List<Cookie>
        {
            new("SID", "secret", "/", ".youtube.com") { Secure = true, Expires = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
            new("PREF", "hl=en", "/", "www.youtube.com"),
        };

        YoutubeCookieService.SaveNetscapeFile(cookies, path);

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("# Netscape HTTP Cookie File", lines[0]);
        Assert.Equal(3, lines.Length);

        var sid = lines[1].Split('\t');
        Assert.Equal(".youtube.com", sid[0]);
        Assert.Equal("TRUE", sid[1]);      // include subdomains
        Assert.Equal("/", sid[2]);
        Assert.Equal("TRUE", sid[3]);      // secure
        Assert.Equal("SID", sid[5]);
        Assert.Equal("secret", sid[6]);
        Assert.NotEqual("0", sid[4]);      // a dated cookie becomes a Unix timestamp

        var pref = lines[2].Split('\t');
        Assert.Equal("www.youtube.com", pref[0]);
        Assert.Equal("FALSE", pref[1]);
        Assert.Equal("0", pref[4]);        // session cookie: yt-dlp writes 0
    }

    [Theory]
    [InlineData("[download]  42.3% of 4.16MiB at 1.2MiB/s ETA 00:03", 42.3)]
    [InlineData("[download] 100% of 4.16MiB in 00:03", 100)]
    [InlineData("[download]   7,5% of 1.00GiB", 7.5)]
    [InlineData("[youtube] iuJDhFRDx9M: Downloading player", 0)]
    [InlineData("", 0)]
    public void TryParseProgress_ReadsYtDlpOutput(string line, double expected)
    {
        var ok = YtDlpDownloader.TryParseProgress(line, out var percent);

        if (expected == 0) Assert.False(ok);
        else
        {
            Assert.True(ok);
            Assert.Equal(expected, percent, 3);
        }
    }

    [Fact]
    public void FilterToYouTube_KeepsTheYoutubeSession()
    {
        var cookies = new List<Cookie>
        {
            new("SID", "v", "/", ".youtube.com"),
            new("SID", "v", "/", ".google.com"),
            new("PREF", "v", "/", ".youtube.com"),
            new("_ga", "v", "/", ".google.com"),      // unrelated analytics
            new("session", "v", "/", ".example.com"),
        };

        var kept = YoutubeCookieService.FilterToYouTube(cookies);

        Assert.Contains(kept, c => c.Name == "SID" && c.Domain == ".youtube.com");
        Assert.Contains(kept, c => c.Name == "PREF" && c.Domain == ".youtube.com");
        Assert.DoesNotContain(kept, c => c.Domain == ".example.com");
        Assert.DoesNotContain(kept, c => c.Name == "_ga");
    }
}
