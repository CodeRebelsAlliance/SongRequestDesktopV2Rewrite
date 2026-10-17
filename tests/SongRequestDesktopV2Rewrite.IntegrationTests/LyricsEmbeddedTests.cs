using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class LyricsEmbeddedTests
{
    [SkippableFact]
    public void GetEmbeddedLyrics_ReadsSyncedLyricsFromTag()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var path = Path.Combine(Fixtures.NewTempDir(), "tagged.wav");
        File.Copy(Fixtures.Audio.Wav, path, overwrite: true);

        using (var tagFile = TagLib.File.Create(path))
        {
            tagFile.Tag.Lyrics = "[00:01.00] First line\n[00:02.00] Second line";
            tagFile.Save();
        }

        var result = LyricsService.GetEmbeddedLyrics(path);

        Assert.NotNull(result);
        Assert.True(result!.Found);
        Assert.Equal(LyricsProvider.FileEmbedded, result.Provider);
        Assert.True(result.HasSynced);
        Assert.Contains("First line", result.SyncedLyrics);
    }

    [SkippableFact]
    public void GetEmbeddedLyrics_PlainLyricsWithNoTimestamps_ArePlain()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var path = Path.Combine(Fixtures.NewTempDir(), "tagged-plain.wav");
        File.Copy(Fixtures.Audio.Wav, path, overwrite: true);

        using (var tagFile = TagLib.File.Create(path))
        {
            tagFile.Tag.Lyrics = "just plain lyrics without timestamps";
            tagFile.Save();
        }

        var result = LyricsService.GetEmbeddedLyrics(path);

        Assert.NotNull(result);
        Assert.False(result!.HasSynced);
        Assert.Contains("plain lyrics", result.PlainLyrics);
    }

    [SkippableFact]
    public void GetEmbeddedLyrics_NoLyrics_ReturnsNull()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var path = Path.Combine(Fixtures.NewTempDir(), "untagged.wav");
        File.Copy(Fixtures.Audio.Wav, path, overwrite: true);

        Assert.Null(LyricsService.GetEmbeddedLyrics(path));
    }
}
