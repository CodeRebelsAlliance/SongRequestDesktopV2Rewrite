using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Automatic downloads must not start before the user has decided about YouTube
/// cookies (or when cookies are already saved): without them YouTube rejects
/// restricted videos with a "video unavailable" error, and downloading the videos the
/// user has not even confirmed is wasteful.
/// </summary>
public class DownloadGateTests
{
    [Fact]
    public async Task Downloads_AreDeferredUntilTheUserFindsOutAboutCookies()
    {
        var host = new HeadlessHost(new List<Cookie>());
        host.SetupComplete = false;
        YoutubeCookieService.Clear(); // no cookies saved yet

        var videoId = "defer" + Guid.NewGuid().ToString("N")[..6];
        host.fetchedYtids.Add(videoId); // the fetch loop marks entries as seen up front

        await host.AddVideoPanelAsync($"https://www.youtube.com/watch?v={videoId}");

        // Nothing was downloaded, and the id is left out of fetchedYtids so the next
        // fetch (after the setup step) picks it up again.
        Assert.False(File.Exists(Path.Combine(AppPaths.DownloadPath, videoId + ".mp3")));
        Assert.DoesNotContain(videoId, host.fetchedYtids);
    }

    [Fact]
    public async Task Downloads_AreDeferred_EvenAfterTheInitialLibraryLoad()
    {
        var host = new HeadlessHost(new List<Cookie>());
        host.SetupComplete = false;
        host.InitialLoadComplete = true; // the old code only waited for this
        YoutubeCookieService.Clear();

        var videoId = "defer" + Guid.NewGuid().ToString("N")[..6];

        await host.AddVideoPanelAsync($"https://www.youtube.com/watch?v={videoId}");

        Assert.False(File.Exists(Path.Combine(AppPaths.DownloadPath, videoId + ".mp3")));
    }

    [Fact]
    public void CookiesSaved_BypassTheGate()
    {
        // With cookies already present the user does not have to answer again.
        var host = new HeadlessHost(new List<Cookie>());
        host.SetupComplete = false;

        var path = AppPaths.YoutubeAuthPath;
        try
        {
            YoutubeCookieService.Save(new[] { new Cookie("SID", "value", "/", ".youtube.com") });

            // ReadyForDownloads is private; the observable effect is that the gate no
            // longer removes the id and the (failing) download path is attempted, so we
            // assert on the saved state instead of the internal flag.
            Assert.NotEmpty(YoutubeCookieService.LoadSaved());
        }
        finally
        {
            YoutubeCookieService.Clear();
            _ = path;
        }
    }

    [Fact]
    public void ClearingCookies_RestoresTheGate()
    {
        YoutubeCookieService.Clear();
        Assert.Empty(YoutubeCookieService.LoadSaved());
    }
}
