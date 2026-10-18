using System;
using System.Diagnostics;
using System.IO;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// The artwork store with the real FFmpeg: it must produce a much smaller image, and it
/// must fail loudly (null) rather than silently returning a full-size one. Guards the
/// converter arguments — a temporary file without an extension needs an explicit output
/// format, which is exactly how the downscaling first broke.
/// </summary>
public class ThumbnailStoreTests
{
    [SkippableFact]
    public void RealFfmpeg_ProducesASmallCachedDataUrl()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var dir = Fixtures.NewTempDir();
        var source = Path.Combine(dir, "artwork.jpg");
        using (var process = Process.Start(new ProcessStartInfo(Fixtures.FfmpegExe,
                   $"-y -hide_banner -loglevel error -f lavfi -i \"testsrc=size=1280x720:rate=1\" -frames:v 1 \"{source}\"")
               {
                   RedirectStandardError = true,
                   UseShellExecute = false,
                   CreateNoWindow = true,
               })!)
        {
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20_000), "ffmpeg did not create the fixture");
            Assert.Equal(0, process.ExitCode);
        }

        var sourceBytes = new FileInfo(source).Length;
        Assert.True(sourceBytes > 20_000, $"fixture too small to be meaningful: {sourceBytes} bytes");

        var store = new ThumbnailStore { CacheDirectory = Path.Combine(dir, "cache") };
        store.BeginRequest(4);

        var url = store.GetDataUrl(source);

        Assert.NotNull(url);
        Assert.StartsWith("data:image/jpeg;base64,", url);

        var encodedBytes = (url!.Length - "data:image/jpeg;base64,".Length) * 3 / 4;
        Assert.True(encodedBytes < sourceBytes,
            $"downscaled artwork ({encodedBytes} bytes) is not smaller than the source ({sourceBytes} bytes)");
        Assert.True(Directory.GetFiles(Path.Combine(dir, "cache")).Length > 0, "nothing was cached on disk");

        // second call is served from memory, no reconversion needed
        var again = store.GetDataUrl(source);
        Assert.Equal(url, again);
    }
}
