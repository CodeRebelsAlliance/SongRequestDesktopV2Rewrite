using System.Collections.Concurrent;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The desktop updates itself from GitHub release archives (download, swap the
/// app folder, relaunch). Mobile must not: the release assets are desktop
/// archives, and an app-store/APK install is the only safe path there.
///
/// These tests pin the guard, so a phone can never be offered the desktop
/// installer — and never runs it.
/// </summary>
public class UpdateFlowTests
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
        string id = "u1",
        Action<JObject>? customize = null)
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
    public async Task CheckForUpdates_WhenTheHostCannotSelfUpdate_ReportsNoUpdate()
    {
        var saved = UpdateService.SelfUpdateSupported;
        try
        {
            // What a mobile host registers at startup.
            UpdateService.SelfUpdateSupported = false;

            var (interop, responses) = Create();
            var response = await Request(interop, responses, "checkForUpdates");
            var result = response["result"];

            Assert.NotNull(result);
            Assert.False(result!["updateAvailable"]!.ToObject<bool>());
            Assert.False(result["selfUpdate"]!.ToObject<bool>());
            Assert.Equal(AppInfo.version, result["currentVersion"]!.ToString());
            // The check must not have talked to GitHub: no asset was selected.
            Assert.True(string.IsNullOrEmpty(result["downloadUrl"]?.ToString()));
            Assert.True(string.IsNullOrEmpty(result["assetName"]?.ToString()));
        }
        finally
        {
            UpdateService.SelfUpdateSupported = saved;
        }
    }

    [Fact]
    public async Task InstallUpdate_WhenTheHostCannotSelfUpdate_IsRefused()
    {
        var saved = UpdateService.SelfUpdateSupported;
        try
        {
            UpdateService.SelfUpdateSupported = false;

            var (interop, responses) = Create();
            var response = await Request(interop, responses, "installUpdate",
                customize: m => m["downloadUrl"] = "https://example.invalid/SongRequest-linux-x64.tar.gz");
            var result = response["result"];

            Assert.NotNull(result);
            Assert.Contains("app store", result!["error"]?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            UpdateService.SelfUpdateSupported = saved;
        }
    }

    [Fact]
    public void SelfUpdate_IsOnForTheDesktopHost()
        => Assert.True(UpdateService.SelfUpdateSupported);
}
