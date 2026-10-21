using System.Collections.Generic;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class LibraryServiceScanTests
{
    [SkippableFact]
    public async Task Scan_FindsAndIndexesAudioFiles()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();
        lib.Data.Config.ScanFolders = new List<string> { Fixtures.Audio.Dir };

        var result = await lib.ScanAsync();

        Assert.True(result.SongsAdded >= 3, $"SongsAdded={result.SongsAdded}, FilesFound={result.FilesFound}");
        Assert.True(lib.Data.Songs.Count >= 3);
        Assert.All(lib.Data.Songs, s => Assert.True(File.Exists(s.FilePath), $"missing file {s.FilePath}"));
    }

    [SkippableFact]
    public void Import_AddsSong_AndRejectsDuplicateByHash()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();

        var song = lib.ImportDownloadedSong(Fixtures.Audio.Wav, youTubeId: "abc123");

        Assert.NotNull(song);
        Assert.False(string.IsNullOrWhiteSpace(song!.Title));
        Assert.Equal("abc123", song.YouTubeId);

        // Same file content -> duplicate hash -> rejected
        Assert.Null(lib.ImportDownloadedSong(Fixtures.Audio.Wav));
    }

    [SkippableFact]
    public void Import_Mp3_ReportsTheSourceBitrate_NotTheDecodedFloatFormat()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();
        var song = lib.ImportDownloadedSong(Fixtures.Audio.Mp3);

        Assert.NotNull(song);
        // A decoded 48 kHz stereo float stream would report 3072 kbps.
        Assert.InRange(song!.BitrateKbps, 32, 512);
    }

    [SkippableFact]
    public void Import_PersistsAcrossInstances()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        LibrarySong? imported;
        using (var lib = new LibraryService())
        {
            imported = lib.ImportDownloadedSong(Fixtures.Audio.Mp3, desiredTitle: "My Song");
        }

        using var reloaded = new LibraryService();
        Assert.Contains(reloaded.Data.Songs, s => s.Id == imported!.Id);
        Assert.Equal("My Song", reloaded.Data.FindSong(imported!.Id)!.Title);
    }

    [SkippableFact]
    public async Task Scan_MarksMissingFiles_AndRemovesThemWhenConfigured()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();
        var dir = Fixtures.NewTempDir();
        var file = Path.Combine(dir, "gone.mp3");
        File.Copy(Fixtures.Audio.Mp3, file);
        var song = lib.ImportDownloadedSong(file);
        Assert.NotNull(song);
        File.Delete(file);

        lib.Data.Config.ScanFolders = new List<string> { Fixtures.Audio.Dir };
        lib.Data.Config.RemoveMissingOnScan = true;
        await lib.ScanAsync();

        Assert.DoesNotContain(lib.Data.Songs, s => s.Id == song!.Id);
    }

    [SkippableFact]
    public async Task Scan_RetainsMissingFiles_WhenNotConfiguredToRemove()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();
        var dir = Fixtures.NewTempDir();
        var file = Path.Combine(dir, "gone.mp3");
        File.Copy(Fixtures.Audio.Mp3, file);
        var song = lib.ImportDownloadedSong(file);
        Assert.NotNull(song);
        File.Delete(file);

        lib.Data.Config.ScanFolders = new List<string> { Fixtures.Audio.Dir };
        lib.Data.Config.RemoveMissingOnScan = false;
        await lib.ScanAsync();

        Assert.Contains(lib.Data.Songs, s => s.Id == song!.Id && s.FileStatus == FileStatus.Missing);
    }

    [SkippableFact]
    public async Task Scan_MissingFolder_ReportsError()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Fixtures.ResetLibrary();

        using var lib = new LibraryService();
        lib.Data.Config.ScanFolders = new List<string> { Path.Combine(Path.GetTempPath(), "definitely-not-a-folder-" + Guid.NewGuid().ToString("N")) };

        var result = await lib.ScanAsync();

        Assert.NotEmpty(result.Errors);
    }
}
