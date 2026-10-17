using System;
using System.IO;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Exercises the real yt-dlp process plumbing used by the browser cookie import:
/// a hung import must be stopped and reported (instead of stalling the UI until its
/// own 60 s request timeout), and a bogus browser must fail cleanly.
/// </summary>
public class CookieImportTests
{
    [SkippableFact]
    public async Task HungImport_IsStoppedByTheTimeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a POSIX shell stub");
        Skip.IfNot(YoutubeCookieService.TryFindYtDlp() != null, "yt-dlp not installed");

        // A fake yt-dlp that never finishes.
        var stub = Path.Combine(Fixtures.NewTempDir(), "slow-yt-dlp.sh");
        File.WriteAllText(stub, "#!/bin/sh\nsleep 120\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var started = DateTime.UtcNow;
        var (ok, cookies, error) = await YoutubeCookieService.GrabFromBrowserAsync(
            "firefox", stub, TimeSpan.FromSeconds(2));
        var elapsed = DateTime.UtcNow - started;

        Assert.False(ok);
        Assert.Empty(cookies);
        Assert.NotNull(error);
        Assert.Contains("took longer than", error!);
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"the hung import was not stopped (took {elapsed.TotalSeconds:0.0}s)");
    }

    [SkippableFact]
    public async Task UnknownBrowser_FailsFastWithAClearError()
    {
        Skip.IfNot(YoutubeCookieService.TryFindYtDlp() != null, "yt-dlp not installed");

        var started = DateTime.UtcNow;
        var (ok, cookies, error) = await YoutubeCookieService.GrabFromBrowserAsync("definitely-not-a-browser");
        var elapsed = DateTime.UtcNow - started;

        Assert.False(ok);
        Assert.Empty(cookies);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.True(elapsed < YoutubeCookieService.ImportTimeout, $"the import should have failed fast (took {elapsed.TotalSeconds:0.0}s)");
    }
}
