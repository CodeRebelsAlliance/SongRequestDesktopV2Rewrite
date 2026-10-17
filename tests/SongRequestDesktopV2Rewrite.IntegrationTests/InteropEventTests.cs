using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Drives the interop event bus end-to-end through a real library scan.
/// </summary>
public class InteropEventTests
{
    [SkippableFact]
    public async Task ScanLibrary_EmitsProgressAndCompletedEvents()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        // ScanLibraryAsync calls SyncConfig(), which reloads scan folders from config.
        ConfigService.Instance.Update(c => c.LibraryScanFolders = new List<string> { Fixtures.Audio.Dir });

        var messages = new ConcurrentQueue<string>();
        interop.SendMessage = messages.Enqueue;

        interop.HandleMessage("{\"type\":\"request\",\"id\":\"s1\",\"method\":\"scanLibrary\"}");

        bool completed = false;
        for (int i = 0; i < 400 && !completed; i++)
        {
            while (messages.TryDequeue(out var json))
            {
                var obj = JObject.Parse(json);
                if (obj["type"]?.ToString() == "event" &&
                    obj["eventName"]?.ToString() == "libraryScanCompleted")
                {
                    completed = true;
                }
            }

            if (!completed) await Task.Delay(20);
        }

        Assert.True(completed, "libraryScanCompleted event was not received");
    }
}
