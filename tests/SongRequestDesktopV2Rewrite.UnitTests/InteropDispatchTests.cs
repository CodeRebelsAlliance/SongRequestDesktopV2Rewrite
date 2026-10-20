using System.Collections.Concurrent;
using System.IO;
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

    private static async Task<JObject> Request(YoutubeFormInterop interop, ConcurrentQueue<string> responses, string method, string id = "t1", Action<JObject>? customize = null)
    {
        var msg = new JObject { ["type"] = "request", ["id"] = id, ["method"] = method };
        customize?.Invoke(msg);
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
    public async Task GetHomeFeed_ReturnsTheFourHomeRows()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getHomeFeed");
        var result = response["result"];

        Assert.NotNull(result);
        Assert.NotNull(result!["playlists"]);
        Assert.NotNull(result["recommended"]);
        Assert.NotNull(result["recentlyPlayed"]);
        Assert.NotNull(result["newlyAdded"]);
        Assert.NotNull(result["stats"]);
    }

    [Fact]
    public async Task GetMusicAudioDevices_AlwaysListsTheSystemDefault()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "getMusicAudioDevices");
        var devices = response["result"]?["devices"] as JArray;

        Assert.NotNull(devices);
        Assert.Contains(devices!, d => d["number"]?.ToObject<int>() == -1);
    }

    [Fact]
    public async Task SoundboardBrowseFile_ResponseGoesToTheRequestingWindow_AfterAwait()
    {
        var (_, interop, defaultResponses) = Create();
        var targeted = new ConcurrentQueue<string>();

        var temp = Path.Combine(Path.GetTempPath(), "sr-sound-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.WriteAllBytes(temp, new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 });
            interop.PickSoundFile = async _ =>
            {
                await Task.Delay(50);       // forces the async continuation onto another thread
                return temp;
            };

            var msg = new JObject
            {
                ["type"] = "request",
                ["id"] = "browse-1",
                ["method"] = "soundboardBrowseFile",
                ["row"] = 0,
                ["col"] = 0
            };

            interop.HandleMessage(msg.ToString(Formatting.None), targeted.Enqueue);

            for (int i = 0; i < 300 && targeted.IsEmpty; i++) await Task.Delay(10);
            Assert.True(targeted.TryDequeue(out var json), "no response reached the requesting page");

            var response = JObject.Parse(json!);
            Assert.Equal("browse-1", response["id"]?.ToString());
            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true);
            Assert.Empty(defaultResponses);

            var fileName = response["result"]?["fileName"]?.ToString();
            if (!string.IsNullOrEmpty(fileName))
            {
                try { File.Delete(Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), fileName)); } catch { }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    [Fact]
    public async Task SetMusicOutputDevice_AppliesTheChoiceToTheEngine()
    {
        var (host, interop, responses) = Create();

        var response = await Request(interop, responses, "setMusicOutputDevice",
            customize: m => m["deviceNumber"] = 7);

        Assert.True(response["result"]?["success"]?.ToObject<bool>());
        Assert.Equal(7, host.Music.OutputDeviceNumber);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        var (_, interop, responses) = Create();

        var response = await Request(interop, responses, "thisMethodDoesNotExist");

        Assert.Contains("Unknown method", response["result"]?["error"]?.ToString());
    }

    [Fact]
    public async Task GetStartupData_ReturnsAResponse()    {
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
    public async Task Quit_Command_InvokesTheHostQuitHandler()
    {
        var (_, interop, responses) = Create();
        var quitCalls = 0;
        interop.QuitRequested = () => quitCalls++;

        try
        {
            var response = await Request(interop, responses, "quit", "q1");

            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true);
            Assert.Equal(1, quitCalls);
        }
        finally
        {
            interop.QuitRequested = null;
            while (responses.TryDequeue(out _)) { }
        }
    }

    [Fact]
    public async Task SaveSettings_ClearingTheCrashGuardLetsTheTrayBeRetried()
    {
        var (_, interop, responses) = Create();
        StartupGuard.DisableTray();
        try
        {
            Assert.True(StartupGuard.TrayDisabled);

            var response = await Request(interop, responses, "saveSettings", "tray1", msg =>
            {
                msg["settings"] = new JObject { ["minimizeToTray"] = false, ["closeToTray"] = false };
            });

            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true, response["result"]?.ToString());
            Assert.False(StartupGuard.TrayDisabled);
        }
        finally
        {
            StartupGuard.ClearTrayDisabled();
            while (responses.TryDequeue(out _)) { }
        }
    }

    [Fact]
    public async Task SaveRemoteConfig_ToleratesNullValuesFromTheWebUi()
    {
        var (_, interop, responses) = Create();
        var previous = ConfigService.Instance.Current.RemoteControl.MidiInputDevice;
        try
        {
            // The web UI cannot express "no value" for a number: parseInt('') is NaN and
            // JSON.stringify(NaN) is null. This used to fail the whole save with
            // "Can not convert Null to Int32." when no MIDI device was selected.
            var response = await Request(interop, responses, "saveRemoteConfig", "null1", msg =>
            {
                msg["midiEnabled"] = false;
                msg["midiInputDevice"] = JValue.CreateNull();
                msg["midiOutputDevice"] = JValue.CreateNull();
            });

            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true, response["result"]?.ToString());
            Assert.Equal(previous, ConfigService.Instance.Current.RemoteControl.MidiInputDevice);
        }
        finally
        {
            while (responses.TryDequeue(out _)) { }
        }
    }

    [Fact]
    public async Task SaveSettings_ToleratesNullValuesFromTheWebUi()
    {
        var (_, interop, responses) = Create();
        var previous = ConfigService.Instance.Current.Threads;
        try
        {
            var response = await Request(interop, responses, "saveSettings", "null2", msg =>
            {
                msg["settings"] = new JObject
                {
                    ["fetchingTimer"] = JValue.CreateNull(),
                    ["threads"] = JValue.CreateNull(),
                    ["normalizeVolume"] = JValue.CreateNull(),
                    ["libraryScanFolders"] = JValue.CreateNull(),
                };
            });

            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true, response["result"]?.ToString());
            Assert.Equal(previous, ConfigService.Instance.Current.Threads);
        }
        finally
        {
            while (responses.TryDequeue(out _)) { }
        }
    }

    [Fact]
    public async Task CompleteSetup_MarksTheHostReadyForDownloads()
    {
        var (host, interop, responses) = Create();
        try
        {
            Assert.False(host.SetupComplete);

            var response = await Request(interop, responses, "completeSetup", "cs1",
                msg => msg["startupMode"] = "SongRequests");

            Assert.True(response["result"]?["success"]?.ToObject<bool>() == true);
            Assert.True(host.SetupComplete);
        }
        finally
        {
            host.SetupComplete = false;
            while (responses.TryDequeue(out _)) { }
        }
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
