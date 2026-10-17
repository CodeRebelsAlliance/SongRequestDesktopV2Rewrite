using System.IO.Compression;
using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class ToolManagerTests
{
    // ── Download specs ─────────────────────────────────────────────────────

    [Fact]
    public void FfmpegBundles_Windows_UsesGyanZipWithBothBinaries()
    {
        var bundle = Assert.Single(ToolManager.FfmpegBundles("win-x64"));

        Assert.True(bundle.IsArchive);
        Assert.Contains("gyan.dev", bundle.Url);
        Assert.Contains(bundle.Members, m => m.TargetFileName == "ffmpeg.exe");
        Assert.Contains(bundle.Members, m => m.TargetFileName == "ffprobe.exe");
        Assert.DoesNotContain(bundle.Members, m => m.Executable);
    }

    [Fact]
    public void FfmpegBundles_Linux_UsesMatchingArchitectureAndExecutableFlag()
    {
        var x64 = Assert.Single(ToolManager.FfmpegBundles("linux-x64"));
        var arm64 = Assert.Single(ToolManager.FfmpegBundles("linux-arm64"));

        Assert.Contains("linux64", x64.Url);
        Assert.Contains("linuxarm64", arm64.Url);
        Assert.True(x64.IsArchive);
        Assert.All(x64.Members, m => Assert.True(m.Executable));
    }

    [Fact]
    public void FfmpegBundles_MacOs_DownloadsFfmpegAndFfprobeSeparately()
    {
        var bundles = ToolManager.FfmpegBundles("osx-arm64");

        Assert.Equal(2, bundles.Count);
        Assert.Contains(bundles, b => b.Members.Any(m => m.TargetFileName == "ffmpeg"));
        Assert.Contains(bundles, b => b.Members.Any(m => m.TargetFileName == "ffprobe"));
        Assert.All(bundles, b => Assert.Contains("evermeet.cx", b.Url));
    }

    [Fact]
    public void YtDlpBundles_MatchPlatform()
    {
        var win = Assert.Single(ToolManager.YtDlpBundles("win-x64"));
        Assert.False(win.IsArchive);
        Assert.Equal("yt-dlp.exe", win.Members[0].TargetFileName);
        Assert.False(win.Members[0].Executable);

        var unix = Assert.Single(ToolManager.YtDlpBundles("linux-x64"));
        Assert.False(unix.IsArchive);
        Assert.Equal("yt-dlp", unix.Members[0].TargetFileName);
        Assert.True(unix.Members[0].Executable);
    }

    // ── Install orchestration ──────────────────────────────────────────────

    private static Func<string, string, Action<double>, CancellationToken, Task> ZipDownload(params (string Name, string Content)[] files)
    {
        return (url, destination, progress, ct) =>
        {
            var staging = Path.Combine(Path.GetTempPath(), "sr-test-zip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var (name, content) in files) File.WriteAllText(Path.Combine(staging, name), content);

            if (File.Exists(destination)) File.Delete(destination);
            ZipFile.CreateFromDirectory(staging, destination);
            progress?.Invoke(100);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task InstallCore_ExtractsArchiveMembersIntoTheTargetDirectory()
    {
        var target = TestSupport.NewTempDir();
        var stages = new List<string>();
        var bundle = new ToolBundle("https://example.com/ff.zip", true, new[]
        {
            new ToolMember("ffmpeg", "ffmpeg", true),
            new ToolMember("ffprobe", "ffprobe", true),
        });

        bool ok = await ToolManager.InstallCoreAsync(
            new[] { bundle },
            target,
            "ffmpeg",
            p => stages.Add(p.Stage),
            download: ZipDownload(("ffmpeg", "a"), ("ffprobe", "b")),
            extract: UpdateInstaller.ExtractArchiveAsync,
            makeExecutable: (_, _) => { });

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(target, "ffmpeg")));
        Assert.True(File.Exists(Path.Combine(target, "ffprobe")));
        Assert.Contains("downloading", stages);
        Assert.Contains("extracting", stages);
        Assert.Contains("installing", stages);
        Assert.Contains("done", stages);
    }

    [Fact]
    public async Task InstallCore_RawBinary_IsCopiedAndMarkedExecutable()
    {
        var target = TestSupport.NewTempDir();
        string? chmodPath = null;
        bool chmodExecutable = false;
        var bundle = new ToolBundle("https://example.com/yt-dlp", false, new[] { new ToolMember("yt-dlp", "yt-dlp", true) });

        bool ok = await ToolManager.InstallCoreAsync(
            new[] { bundle },
            target,
            "yt-dlp",
            null,
            download: (url, destination, progress, ct) => { File.WriteAllText(destination, "binary"); progress?.Invoke(100); return Task.CompletedTask; },
            extract: (archive, dir) => Task.CompletedTask,
            makeExecutable: (path, executable) => { chmodPath = path; chmodExecutable = executable; });

        Assert.True(ok);
        Assert.Equal("binary", File.ReadAllText(Path.Combine(target, "yt-dlp")));
        Assert.Equal(Path.Combine(target, "yt-dlp"), chmodPath);
        Assert.True(chmodExecutable);
    }

    [Fact]
    public async Task InstallCore_DownloadFailure_ReportsErrorAndReturnsFalse()
    {
        var stages = new List<string>();

        bool ok = await ToolManager.InstallCoreAsync(
            new[] { new ToolBundle("u", false, new[] { new ToolMember("t", "t", false) }) },
            TestSupport.NewTempDir(),
            "t",
            p => stages.Add(p.Stage),
            download: (url, destination, progress, ct) => throw new InvalidOperationException("boom"),
            extract: (archive, dir) => Task.CompletedTask,
            makeExecutable: (path, executable) => { });

        Assert.False(ok);
        Assert.Contains("error", stages);
    }

    [Fact]
    public void FindFile_SearchesRecursively()
    {
        var dir = TestSupport.NewTempDir();
        var nested = Path.Combine(dir, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "ffmpeg"), "x");

        Assert.Equal(Path.Combine(nested, "ffmpeg"), ToolManager.FindFile(dir, "ffmpeg"));
        Assert.Null(ToolManager.FindFile(dir, "missing"));
    }

    // ── Download ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadFileAsync_WritesTheFileAndReportsProgress()
    {
        using var client = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, new string('a', 10000)));
        var destination = Path.Combine(TestSupport.NewTempDir(), "tool.bin");
        var progress = new List<double>();

        await ToolManager.DownloadFileAsync("https://example.com/tool", destination, progress.Add, client);

        Assert.Equal(10000, new FileInfo(destination).Length);
        Assert.Equal(100.0, progress[^1], 3);
    }

    // ── Status / locator ───────────────────────────────────────────────────

    [Fact]
    public void ToolStatus_FlagsDerivedCorrectly()
    {
        var ready = new ToolStatus
        {
            Ffmpeg = new ToolStatusEntry(true, "/f"),
            Ffprobe = new ToolStatusEntry(true, "/p"),
            YtDlp = new ToolStatusEntry(true, "/y"),
        };
        Assert.True(ready.AudioReady);
        Assert.True(ready.AllPresent);

        var noYt = new ToolStatus
        {
            Ffmpeg = new ToolStatusEntry(true, "/f"),
            Ffprobe = new ToolStatusEntry(true, "/p"),
            YtDlp = new ToolStatusEntry(false, null),
        };
        Assert.True(noYt.AudioReady);
        Assert.False(noYt.AllPresent);

        var noFf = new ToolStatus { Ffmpeg = new ToolStatusEntry(false, null) };
        Assert.False(noFf.AudioReady);
    }

    [Fact]
    public void ToolLocator_FindsToolsInTheUserToolsDirectory()
    {
        ToolLocator.Invalidate();
        var name = OperatingSystem.IsWindows() ? "sr-probe-tool.exe" : "sr-probe-tool";
        var directory = AppPaths.UserToolsDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");

        try
        {
            Assert.Equal(path, ToolLocator.Find("sr-probe-tool"));
        }
        finally
        {
            File.Delete(path);
            ToolLocator.Invalidate();
        }
    }
}
