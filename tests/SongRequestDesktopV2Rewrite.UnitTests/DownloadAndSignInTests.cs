using System.Collections.Concurrent;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Mobile downloads: which container is fetched, where the file goes, and the
/// in-app sign-in that replaces the desktop's browser-cookie import.
/// </summary>
public class DownloadAndSignInTests
{
    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
        private readonly string _download = AppPaths.DownloadPath;
        private readonly string _dataRoot = AppPaths.DataRoot;
        private readonly string? _container = DownloadedAudio.PreferredContainer;

        public string Downloads { get; }

        public Sandbox()
        {
            Downloads = Path.Combine(_root, "downloadedvideos");
            Directory.CreateDirectory(Downloads);
            AppPaths.DataRoot = _root;
            AppPaths.DownloadPath = Downloads;
        }

        public void Dispose()
        {
            DownloadedAudio.PreferredContainer = _container;
            AppPaths.DownloadPath = _download;
            AppPaths.DataRoot = _dataRoot;
        }
    }

    [Fact]
    public void PreferredExtension_DefaultsToTheLegacyMp3_AndFollowsTheContainer()
    {
        var saved = DownloadedAudio.PreferredContainer;
        try
        {
            DownloadedAudio.PreferredContainer = null;
            Assert.Equal(".mp3", DownloadedAudio.PreferredExtension);

            DownloadedAudio.PreferredContainer = "mp4";     // what the mobile host sets
            Assert.Equal(".m4a", DownloadedAudio.PreferredExtension);

            DownloadedAudio.PreferredContainer = "webm";
            Assert.Equal(".webm", DownloadedAudio.PreferredExtension);
        }
        finally
        {
            DownloadedAudio.PreferredContainer = saved;
        }
    }

    [Fact]
    public void MatchesPreferredContainer_TreatsMp4AndM4aAsTheSameThing()
    {
        var saved = DownloadedAudio.PreferredContainer;
        try
        {
            DownloadedAudio.PreferredContainer = "mp4";
            Assert.True(DownloadedAudio.MatchesPreferredContainer("mp4"));
            Assert.True(DownloadedAudio.MatchesPreferredContainer("m4a"));   // AAC audio stream
            Assert.False(DownloadedAudio.MatchesPreferredContainer("webm"));
            Assert.False(DownloadedAudio.MatchesPreferredContainer(null));

            // No preference: nothing matches, so the historical choice is used.
            DownloadedAudio.PreferredContainer = null;
            Assert.False(DownloadedAudio.MatchesPreferredContainer("mp4"));
        }
        finally
        {
            DownloadedAudio.PreferredContainer = saved;
        }
    }

    [Fact]
    public void PathFor_ReusesAnExistingDownloadWhicheverExtensionItHas()
    {
        using var sandbox = new Sandbox();
        DownloadedAudio.PreferredContainer = "mp4";

        // A library built by the desktop: the file is an .mp3 (whatever it holds).
        var legacy = Path.Combine(sandbox.Downloads, "abc12345678.mp3");
        File.WriteAllText(legacy, "legacy");

        Assert.Equal(legacy, DownloadedAudio.PathFor("abc12345678"));
        Assert.Equal(legacy, DownloadedAudio.Existing("abc12345678"));
    }

    [Fact]
    public void PathFor_UsesThePreferredExtensionForNewDownloads()
    {
        using var sandbox = new Sandbox();

        DownloadedAudio.PreferredContainer = null;
        Assert.EndsWith("new12345678.mp3", DownloadedAudio.PathFor("new12345678"));

        DownloadedAudio.PreferredContainer = "mp4";
        Assert.EndsWith("new12345678.m4a", DownloadedAudio.PathFor("new12345678"));

        Assert.Null(DownloadedAudio.Existing("new12345678"));
    }

    private static (HeadlessHost Host, YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (host, interop, responses);
    }

    private static async Task<JObject> Request(
        YoutubeFormInterop interop,
        ConcurrentQueue<string> responses,
        string method,
        string id = "s1",
        JObject? payload = null)
    {
        var msg = new JObject { ["type"] = "request", ["id"] = id, ["method"] = method };
        if (payload != null)
            foreach (var property in payload.Properties())
                msg[property.Name] = property.Value;

        interop.HandleMessage(msg.ToString(Formatting.None));

        for (int i = 0; i < 400; i++)
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

    [Fact]
    public async Task DownloadVideoAsync_ReusesAnExistingFile()
    {
        using var sandbox = new Sandbox();
        var existing = Path.Combine(sandbox.Downloads, "dQw4w9WgXcQ.mp3");
        var payload = new byte[64 * 1024];
        payload[0] = 1;
        await File.WriteAllBytesAsync(existing, payload);

        // No cookies, no network: an existing file must be returned before YouTube
        // is contacted at all (this used to re-download and overwrite it).
        var service = new YoutubeService(Array.Empty<Cookie>());
        var path = await service.DownloadVideoAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", sandbox.Downloads);

        Assert.Equal(existing, path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(existing));
    }

    [Fact]
    public void ThumbnailUrl_IsDerivedFromTheId_WithoutMetadataRequests()
    {
        const string id = "dQw4w9WgXcQ";
        Assert.Equal($"https://i.ytimg.com/vi/{id}/maxresdefault.jpg", YoutubeService.GetThumbnailUrl(id));
        Assert.Equal($"https://i.ytimg.com/vi/{id}/hqdefault.jpg", YoutubeService.GetThumbnailUrl(id, highQuality: false));
        Assert.Equal(string.Empty, YoutubeService.GetThumbnailUrl(""));
    }

    [Fact]
    public async Task GetThumbnail_ServesTheCachedImage()
    {
        using var sandbox = new Sandbox();
        var directory = AppPaths.ThumbnailCachePath;
        Directory.CreateDirectory(directory);
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 7, 8, 9 };
        await File.WriteAllBytesAsync(Path.Combine(directory, "thumbcache1.jpg"), bytes);

        var (_, interop, responses) = Create();
        var response = await Request(interop, responses, "getThumbnail", "t1", new JObject { ["videoId"] = "thumbcache1" });

        Assert.Equal(Convert.ToBase64String(bytes), response["result"]!["data"]!.ToString());
        Assert.Equal("image/jpeg", response["result"]!["mime"]!.ToString());
    }

    [Fact]
    public async Task GetLyrics_ReturnsFindingsFromTheCache()
    {
        using var sandbox = new Sandbox();
        var (host, interop, responses) = Create();

        var data = host.Engine.Cache.GetOrCreate("lyriccache1");
        data.Title = "Cached Song";
        data.Creator = "Cached Artist";
        data.LyricsText = "cached words";
        data.LyricsFetched = true;

        var response = await Request(interop, responses, "getLyrics", "l1", new JObject { ["videoId"] = "lyriccache1" });

        Assert.Equal("cached words", response["result"]!["lyrics"]!.ToString());
        Assert.True(response["result"]!["cached"]!.ToObject<bool>());
    }

    [Fact]
    public async Task DownloadAndProcess_ReusesEveryCache_ForAnAlreadyDownloadedSong()
    {
        using var sandbox = new Sandbox();
        var cfg = ConfigService.Instance.Current!;
        var autoAdd = cfg.LibraryAutoAddDownloads;
        cfg.LibraryAutoAddDownloads = false;

        try
        {
            const string videoId = "cachedvid01";   // 11 chars, deliberately not a real video

            var existing = Path.Combine(sandbox.Downloads, videoId + ".mp3");
            var payload = new byte[64 * 1024];
            payload[0] = 1;
            await File.WriteAllBytesAsync(existing, payload);

            var (host, interop, responses) = Create();

            var data = host.Engine.Cache.GetOrCreate(videoId);
            data.Title = "Cached Song";
            data.Creator = "Cached Artist";
            data.DurationTicks = TimeSpan.FromSeconds(42).Ticks;
            data.SubtitlesFetched = true;

            var thumbDirectory = AppPaths.ThumbnailCachePath;
            Directory.CreateDirectory(thumbDirectory);
            await File.WriteAllBytesAsync(Path.Combine(thumbDirectory, videoId + ".jpg"), new byte[] { 0xFF, 0xD8 });

            // Every stage is cached: metadata, audio, artwork and lyrics. A single
            // network call would fail for this non-existent video, so success proves
            // nothing was fetched again.
            var response = await Request(interop, responses, "downloadAndProcess", "d1",
                new JObject { ["ytid"] = videoId, ["action"] = "queue" });

            Assert.True(response["result"]!["success"]!.ToObject<bool>());
            Assert.Equal(existing, response["result"]!["path"]!.ToString());
            Assert.Equal(payload, await File.ReadAllBytesAsync(existing));
        }
        finally
        {
            cfg.LibraryAutoAddDownloads = autoAdd;
        }
    }

    [Fact]
    public async Task SignIn_StoresTheHarvestedCookiesAndAppliesThemToTheService()
    {
        var (host, interop, responses) = Create();
        var savedCookies = YoutubeCookieService.LoadSaved();
        var authPath = AppPaths.YoutubeAuthPath;
        var backup = File.Exists(authPath) ? File.ReadAllText(authPath) : null;

        try
        {
            var cookies = new List<Cookie>
            {
                new("SID", "session-value", "/", ".youtube.com"),
                new("HSID", "another", "/", ".youtube.com"),
            };
            interop.SignInAsync = () => Task.FromResult<List<Cookie>?>(cookies);

            var response = await Request(interop, responses, "signIn");
            var result = response["result"];

            Assert.True(result!["success"]!.ToObject<bool>());
            Assert.Equal(2, result["cookieCount"]!.ToObject<int>());

            // Stored for the next start, and live on the service that downloads.
            Assert.Equal(2, YoutubeCookieService.LoadSaved().Count);
            Assert.Equal(2, host.YoutubeService.Cookies.Count);
        }
        finally
        {
            interop.SignInAsync = null;
            if (backup != null) File.WriteAllText(authPath, backup);
            else YoutubeCookieService.Clear();
            _ = savedCookies;
        }
    }

    [Fact]
    public async Task SignIn_ReportsCancellationAndAnUnsupportedHost()
    {
        var (_, interop, responses) = Create();

        // No sign-in delegate (the desktop): a clear error, not a crash.
        var unsupported = await Request(interop, responses, "signIn");
        Assert.Contains("not available", unsupported["result"]!["error"]!.ToString());

        // Cancelled login.
        interop.SignInAsync = () => Task.FromResult<List<Cookie>?>(null);
        var cancelled = await Request(interop, responses, "signIn", id: "s2");
        Assert.True(cancelled["result"]!["cancelled"]!.ToObject<bool>());
        interop.SignInAsync = null;
    }

    [Fact]
    public async Task GetAuthBrowsers_ReportsTheHostsBrowsersAndSignInSupport()
    {
        var (_, interop, responses) = Create();

        // Desktop: browsers to import from, no in-app sign-in.
        var desktop = await Request(interop, responses, "getAuthBrowsers");
        Assert.NotEmpty(desktop["result"]!["browsers"]!.ToArray());
        Assert.False(desktop["result"]!["signInSupported"]!.ToObject<bool>());

        // Mobile: no browsers, sign-in available.
        interop.AuthBrowsersProvider = () => new List<string>();
        interop.SignInAsync = () => Task.FromResult<List<Cookie>?>(null);
        var mobile = await Request(interop, responses, "getAuthBrowsers", id: "b2");
        Assert.Empty(mobile["result"]!["browsers"]!.ToArray());
        Assert.True(mobile["result"]!["signInSupported"]!.ToObject<bool>());
        interop.SignInAsync = null;
        interop.AuthBrowsersProvider = null;
    }

    [Fact]
    public async Task GetToolStatus_ReportsAnUnsupportedHostAsNothingToInstall()
    {
        var (_, interop, responses) = Create();
        var saved = ToolManager.IsSupported;
        try
        {
            ToolManager.IsSupported = false;   // what the mobile host sets

            var response = await Request(interop, responses, "getToolStatus");
            var result = response["result"];

            Assert.False(result!["supported"]!.ToObject<bool>());
            Assert.True(result["audioReady"]!.ToObject<bool>());   // so no prompt appears
        }
        finally
        {
            ToolManager.IsSupported = saved;
        }
    }
}
