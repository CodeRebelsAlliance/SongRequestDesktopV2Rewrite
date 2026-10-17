using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class LyricsServiceTests
{
    [Fact]
    public void ParseSyncedLines_ParsesMmSsAndHhMmSs()
    {
        var result = new LyricsResult
        {
            SyncedLyrics = "[00:10.00] Hello\n[01:02:03.50] World\njust some text",
        };

        var lines = result.ParseSyncedLines();

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].Time);
        Assert.Equal("Hello", lines[0].Text);
        Assert.Equal(new TimeSpan(1, 2, 3) + TimeSpan.FromSeconds(0.5), lines[1].Time);
        Assert.Equal("World", lines[1].Text);
    }

    [Fact]
    public void ParseSyncedLines_EmptyWhenNoSyncedLyrics()
    {
        var result = new LyricsResult();
        Assert.False(result.HasSynced);
        Assert.Empty(result.ParseSyncedLines());
    }

    [Fact]
    public void GetLrcFileLyrics_ReadsSyncedLrc()
    {
        var dir = TestSupport.NewTempDir();
        var audio = Path.Combine(dir, "song.mp3");
        File.WriteAllBytes(audio, new byte[] { 0 });
        File.WriteAllText(Path.Combine(dir, "song.lrc"), "[00:01.00] Line one\n[00:02.00] Line two");

        var result = LyricsService.GetLrcFileLyrics(audio);

        Assert.NotNull(result);
        Assert.True(result!.Found);
        Assert.Equal(LyricsProvider.LrcFile, result.Provider);
        Assert.True(result.HasSynced);
    }

    [Fact]
    public void GetLrcFileLyrics_PlainText_WhenNoTimestamps()
    {
        var dir = TestSupport.NewTempDir();
        var audio = Path.Combine(dir, "song.mp3");
        File.WriteAllBytes(audio, new byte[] { 0 });
        File.WriteAllText(Path.Combine(dir, "song.lrc"), "just plain lyrics");

        var result = LyricsService.GetLrcFileLyrics(audio);

        Assert.NotNull(result);
        Assert.Equal("just plain lyrics", result!.PlainLyrics);
        Assert.False(result.HasSynced);
    }

    [Fact]
    public void GetLrcFileLyrics_MissingLrc_ReturnsNull()
    {
        var dir = TestSupport.NewTempDir();
        var audio = Path.Combine(dir, "song.mp3");
        File.WriteAllBytes(audio, new byte[] { 0 });

        Assert.Null(LyricsService.GetLrcFileLyrics(audio));
    }

    [Fact]
    public async Task GetLyricsAsync_ParsesSuccessfulResponse()
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK,
            "{\"trackName\":\"T\",\"artistName\":\"A\",\"duration\":123,\"instrumental\":false,\"plainLyrics\":\"p\",\"syncedLyrics\":\"[00:01.00] x\"}");
        var svc = new LyricsService(new HttpClient(handler));

        var result = await svc.GetLyricsAsync("A", "T", TimeSpan.FromSeconds(123));

        Assert.True(result.Found);
        Assert.Equal("T", result.TrackName);
        Assert.Equal("A", result.ArtistName);
        Assert.Equal(123, result.DurationSeconds);
        Assert.Equal(LyricsProvider.LRCLIB, result.Provider);
        Assert.Equal("[00:01.00] x", result.SyncedLyrics);
        Assert.Contains("lrclib.net", handler.LastUrl);
    }

    [Fact]
    public async Task GetLyricsAsync_NotFound_ReturnsNotFound()
    {
        var handler = new StubHttpHandler(HttpStatusCode.NotFound, "{\"message\":\"not found\"}");
        var svc = new LyricsService(new HttpClient(handler));

        var result = await svc.GetLyricsAsync("A", "T", TimeSpan.FromSeconds(1));

        Assert.False(result.Found);
        Assert.Equal(404, result.StatusCode);
    }
}
