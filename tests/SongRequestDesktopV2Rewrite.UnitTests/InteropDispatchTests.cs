using System.Collections.Concurrent;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Drives the interop request/response bus directly (no Photino) and checks the
/// JSON contract of each command.
/// </summary>
public class InteropDispatchTests
{
    private static (HeadlessHost Host, YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (host, interop, responses);
    }

    private static async Task<JObject> Request(YoutubeFormInterop interop, ConcurrentQueue<string> responses, string method, string id = "t1")
    {
        var msg = new JObject { ["type"] = "request", ["id"] = id, ["method"] = method };
        interop.HandleMessage(msg.ToString(Formatting.None));

        for (int i = 0; i < 200; i++)
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
    public async Task GetAuthBrowsers_ReturnsSupportedBrowsers()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getAuthBrowsers");

        var browsers = response["result"]?["browsers"] as JArray;
        Assert.NotNull(browsers);
        Assert.Contains("chrome", browsers!.Select(b => b.ToString()));
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "thisMethodDoesNotExist");

        Assert.Contains("Unknown method", response["result"]?["error"]?.ToString());
    }

    [Fact]
    public async Task GetStartupData_ReturnsAResponse()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getStartupData");

        Assert.NotNull(response["result"]);
    }

    [Fact]
    public async Task GetRemoteConfig_ReturnsAResponse()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getRemoteConfig");

        Assert.NotNull(response["result"]);
        Assert.Equal("t1", response["id"]?.ToString());
    }

    [Theory]
    [InlineData("getVersion")]
    [InlineData("getSettings")]
    [InlineData("getStartupData")]
    [InlineData("getAuthBrowsers")]
    [InlineData("getVolumeCrossfade")]
    [InlineData("getPlayHistory")]
    [InlineData("getPlaylists")]
    [InlineData("getLibraryStats")]
    [InlineData("getLibraryStatus")]
    [InlineData("soundboardGetState")]
    [InlineData("announcementGetState")]
    [InlineData("musicShareGetStatus")]
    public async Task ReadOnlyCommands_ReturnAResponse(string method)
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, method);

        Assert.Equal("t1", response["id"]?.ToString());
        Assert.True(response.ContainsKey("result"), $"no result for {method}");
    }

    [Theory]
    [InlineData("showMusicPlayer")]
    [InlineData("toggleRepeat")]
    [InlineData("shuffleQueue")]
    [InlineData("clearQueue")]
    [InlineData("stop")]
    [InlineData("playPause")]
    [InlineData("soundboardStopAll")]
    [InlineData("clearAuthCookies")]
    [InlineData("closePresentation")]
    [InlineData("closeVisualization")]
    [InlineData("soundboardClose")]
    [InlineData("announcementClose")]
    [InlineData("cancelLibraryScan")]
    public async Task SafeStatefulCommands_ReturnAResponse(string method)
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, method);

        Assert.Equal("t1", response["id"]?.ToString());
        Assert.True(response.ContainsKey("result"), $"no result for {method}");
    }

    [Fact]
    public void SendEvent_EmitsEventEnvelope()
    {
        var (_, interop, responses) = Create();

        interop.SendEvent("unitTestEvent", new { value = 42 });

        Assert.True(responses.TryDequeue(out var json));
        var obj = JObject.Parse(json!);
        Assert.Equal("event", obj["type"]?.ToString());
        Assert.Equal("unitTestEvent", obj["eventName"]?.ToString());
        Assert.Equal(42, obj["data"]?["value"]?.ToObject<int>());
    }

    [Fact]
    public void SendEvent_WithNullData_IsAllowed()
    {
        var (_, interop, responses) = Create();

        interop.SendEvent("noData", null);

        Assert.True(responses.TryDequeue(out var json));
        var obj = JObject.Parse(json!);
        Assert.Equal("event", obj["type"]?.ToString());
        Assert.Equal("noData", obj["eventName"]?.ToString());
    }

    [Fact]
    public async Task GetSettings_IncludesStartupAndWindowOptions()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getSettings");
        var result = response["result"];

        Assert.NotNull(result);
        Assert.NotNull(result!["autoStart"]);
        Assert.NotNull(result["startMinimized"]);
        Assert.NotNull(result["minimizeToTray"]);
        Assert.NotNull(result["closeToTray"]);
        Assert.NotNull(result["traySupported"]);
        Assert.NotNull(result["autoStartSupported"]);
        Assert.NotNull(result["globalKeyboardShortcuts"]);
        Assert.NotNull(result["globalShortcutsSupported"]);
    }

    [Fact]
    public async Task GetToolStatus_ReportsEachToolAndFlags()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getToolStatus");
        var result = response["result"];

        Assert.NotNull(result);
        Assert.NotNull(result!["ffmpeg"]);
        Assert.NotNull(result["ffprobe"]);
        Assert.NotNull(result["ytDlp"]);
        Assert.NotNull(result["audioReady"]);
        Assert.NotNull(result["allPresent"]);
        Assert.NotNull(result["toolsDirectory"]);
    }

    [Fact]
    public async Task DownloadTools_WithNothingSelected_ReturnsError()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "downloadTools");

        Assert.Contains("Nothing", response["result"]?["error"]?.ToString());
    }

    [Fact]
    public async Task CookieBrowser_IsRememberedAndReportedToTheUI()
    {
        var (_, interop, responses) = Create();
        var previous = ConfigService.Instance.Current.CookieBrowser;
        try
        {
            // The startup prompt's choice is persisted...
            var save = new JObject { ["type"] = "request", ["id"] = "cb1", ["method"] = "saveCookieBrowser", ["browser"] = "firefox" };
            interop.HandleMessage(save.ToString(Formatting.None));
            Assert.Equal("firefox", ConfigService.Instance.Current.CookieBrowser);

            // ...and comes back to both the startup prompt and the settings UI, so the
            // picker defaults to it instead of the first browser in the list.
            var startup = await Request(interop, responses, "getStartupData", "cb2");
            Assert.Equal("firefox", startup["result"]?["cookieBrowser"]?.ToString());

            var settings = await Request(interop, responses, "getSettings", "cb3");
            Assert.Equal("firefox", settings["result"]?["cookieBrowser"]?.ToString());
        }
        finally
        {
            ConfigService.Instance.Update(cfg => cfg.CookieBrowser = previous);

            while (responses.TryDequeue(out _)) { }
        }
    }
}
