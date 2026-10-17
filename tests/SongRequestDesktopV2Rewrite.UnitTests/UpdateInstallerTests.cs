using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class UpdateInstallerTests
{
    // ── Platform / archive handling ────────────────────────────────────────

    [Theory]
    [InlineData("win-x64", ".zip")]
    [InlineData("win-arm64", ".zip")]
    [InlineData("linux-x64", ".tar.gz")]
    [InlineData("linux-arm64", ".tar.gz")]
    [InlineData("osx-arm64", ".tar.gz")]
    public void ArchiveExtensionFor_MatchesPlatform(string platform, string expected)
        => Assert.Equal(expected, UpdateInstaller.ArchiveExtensionFor(platform));

    [Fact]
    public void LauncherName_MatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? "SongRequest V2.exe" : "SongRequest V2";
        Assert.Equal(expected, UpdateInstaller.LauncherName());
    }

    [Fact]
    public void ResolveLauncher_FallsBackToAppDirectory()
    {
        var launcher = UpdateInstaller.ResolveLauncher("/opt/app");
        Assert.Contains("SongRequest V2", launcher);
        Assert.Contains("opt", launcher);
    }

    // ── Verify ─────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyArchive_AcceptsZipMagic()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "a.zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        UpdateInstaller.VerifyArchive(path); // no throw
    }

    [Fact]
    public void VerifyArchive_AcceptsGzipMagic()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "a.tar.gz");
        File.WriteAllBytes(path, new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        UpdateInstaller.VerifyArchive(path); // no throw
    }

    [Fact]
    public void VerifyArchive_RejectsGarbage()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "a.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 });
        Assert.Throws<InvalidOperationException>(() => UpdateInstaller.VerifyArchive(path));
    }

    [Fact]
    public void VerifyArchive_RejectsMissingFile()
        => Assert.Throws<InvalidOperationException>(() => UpdateInstaller.VerifyArchive("/nope/missing.zip"));

    [Fact]
    public void VerifyArchive_RejectsTruncatedFile()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "tiny.zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        Assert.Throws<InvalidOperationException>(() => UpdateInstaller.VerifyArchive(path));
    }

    // ── Extract ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractArchiveAsync_Zip_RoundTrips()
    {
        var dir = TestSupport.NewTempDir();
        var source = Path.Combine(dir, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "hello");

        var zip = Path.Combine(dir, "a.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var destination = Path.Combine(dir, "out");

        await UpdateInstaller.ExtractArchiveAsync(zip, destination);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsync_TarGz_RoundTrips()
    {
        var dir = TestSupport.NewTempDir();
        var source = Path.Combine(dir, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "hello");

        var tgz = Path.Combine(dir, "a.tar.gz");
        using (var file = File.Create(tgz))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
        }

        var destination = Path.Combine(dir, "out");
        await UpdateInstaller.ExtractArchiveAsync(tgz, destination);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    // ── Orchestration ──────────────────────────────────────────────────────

    [Fact]
    public async Task InstallCore_RunsAllStagesInOrder_ThenExits()
    {
        var stages = new List<string>();
        string? downloadedUrl = null;
        string? verifiedPath = null;
        string? extractedArchive = null;
        string? stagingPath = null;
        bool exited = false;

        await UpdateInstaller.InstallCoreAsync(
            "https://example.com/update.tar.gz",
            progress => stages.Add(progress.Stage),
            ".tar.gz",
            download: (url, destination, progress) => { downloadedUrl = url; progress?.Invoke(50); return Task.CompletedTask; },
            verify: path => { verifiedPath = path; return Task.CompletedTask; },
            extract: (archive, destination) => { extractedArchive = archive; return Task.CompletedTask; },
            startUpdater: path => stagingPath = path,
            exit: () => exited = true);

        Assert.Equal("https://example.com/update.tar.gz", downloadedUrl);
        Assert.EndsWith(".tar.gz", verifiedPath);
        Assert.EndsWith(".tar.gz", extractedArchive);
        Assert.False(string.IsNullOrEmpty(stagingPath));
        Assert.True(exited);

        Assert.Equal(UpdateInstaller.Stages.Starting, stages.First());
        Assert.Equal(UpdateInstaller.Stages.Done, stages.Last());
        foreach (var stage in new[]
        {
            UpdateInstaller.Stages.Starting, UpdateInstaller.Stages.Downloading,
            UpdateInstaller.Stages.Verifying, UpdateInstaller.Stages.Extracting,
            UpdateInstaller.Stages.Preparing, UpdateInstaller.Stages.Restarting,
            UpdateInstaller.Stages.Done,
        })
        {
            Assert.Contains(stage, stages);
        }
    }

    [Fact]
    public async Task InstallCore_DownloadFailure_ReportsErrorAndRethrows()
    {
        var stages = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.InstallCoreAsync(
            "https://example.com/x.zip",
            progress => stages.Add(progress.Stage),
            ".zip",
            download: (url, destination, progress) => throw new InvalidOperationException("boom"),
            verify: _ => Task.CompletedTask,
            extract: (archive, destination) => Task.CompletedTask,
            startUpdater: _ => { },
            exit: () => { }));

        Assert.Contains(UpdateInstaller.Stages.Error, stages);
    }

    [Fact]
    public async Task InstallCore_VerifyFailure_StopsBeforeExtracting()
    {
        bool extracted = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.InstallCoreAsync(
            "https://example.com/x.zip",
            _ => { },
            ".zip",
            download: (url, destination, progress) => Task.CompletedTask,
            verify: _ => throw new InvalidOperationException("bad archive"),
            extract: (archive, destination) => { extracted = true; return Task.CompletedTask; },
            startUpdater: _ => { },
            exit: () => { }));

        Assert.False(extracted);
    }

    [Fact]
    public async Task InstallCore_EmptyUrl_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.InstallCoreAsync(
            "",
            _ => { },
            ".zip",
            (url, destination, progress) => Task.CompletedTask,
            _ => Task.CompletedTask,
            (archive, destination) => Task.CompletedTask,
            _ => { },
            () => { }));
    }

    // ── Download ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_StreamsToFileAndReportsProgress()
    {
        const int size = 20000;
        var handler = new StubHttpHandler(HttpStatusCode.OK, new string('a', size));
        using var client = new HttpClient(handler);
        var destination = Path.Combine(TestSupport.NewTempDir(), "download.bin");
        var progress = new List<double>();

        await UpdateInstaller.DownloadAsync("https://example.com/update.zip", destination, progress.Add, client);

        Assert.Equal(size, new FileInfo(destination).Length);
        Assert.NotEmpty(progress);
        Assert.Equal(100.0, progress[^1], 3);
    }

    [Fact]
    public async Task DownloadAsync_NonSuccessStatus_Throws()
    {
        var handler = new StubHttpHandler(HttpStatusCode.NotFound, "nope");
        using var client = new HttpClient(handler);
        var destination = Path.Combine(TestSupport.NewTempDir(), "download.bin");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UpdateInstaller.DownloadAsync("https://example.com/missing.zip", destination, _ => { }, client));
    }

    // ── Updater scripts ────────────────────────────────────────────────────

    [Fact]
    public void WindowsUpdaterScript_BacksUpVerifiesRollsBackAndRelaunches()
    {
        var script = UpdateInstaller.BuildWindowsUpdaterScript(
            appPath: @"C:\apps\SongRequest",
            stagingPath: @"C:\temp\stage",
            launcherPath: @"C:\apps\SongRequest\SongRequest V2.exe",
            backupPath: @"C:\temp\backup",
            logPath: @"C:\temp\update.log");

        Assert.Contains("xcopy", script);
        Assert.Contains(@"C:\apps\SongRequest", script);
        Assert.Contains(@"C:\temp\stage", script);
        Assert.Contains(@"C:\temp\backup", script);
        Assert.Contains("taskkill", script);
        Assert.Contains("rolling back", script);
        Assert.Contains("SongRequest V2.exe", script);
        Assert.Contains("start", script);
    }

    [Fact]
    public void UnixUpdaterScript_BacksUpVerifiesRollsBackAndRelaunches()
    {
        var script = UpdateInstaller.BuildUnixUpdaterScript(
            appPath: "/opt/songrequest",
            stagingPath: "/tmp/stage",
            launcherPath: "/opt/songrequest/SongRequest V2",
            backupPath: "/tmp/backup",
            logPath: "/tmp/update.log",
            scriptPath: "/tmp/update_songrequest.sh");

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("cp -Rf", script);
        Assert.Contains("/opt/songrequest", script);
        Assert.Contains("/tmp/stage", script);
        Assert.Contains("/tmp/backup", script);
        Assert.Contains("rolling back", script);
        Assert.Contains("nohup", script);
        Assert.Contains("rm -f \"$SELF\"", script);
    }
}
