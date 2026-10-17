using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class LyricsQueryNormalizerTests
{
    private static Song Track(string artist, string title, string path)
        => new Song(title, artist, null, TimeSpan.FromMinutes(1), path);

    [Fact]
    public void Build_NonDownloadedSong_KeepsArtistAndTitle()
    {
        var q = LyricsQueryNormalizer.Build(Track("Some Artist - Topic", "A Title", "/music/other.mp3"));

        Assert.False(q.IsSentInSong);
        Assert.Equal("Some Artist", q.Artist);
        Assert.Equal("A Title", q.Title);
    }

    [Fact]
    public void Build_SplitsArtistAndTitleOnSeparator()
    {
        var q = LyricsQueryNormalizer.Build(
            Track("ignored", "Cool Artist - Nice Song (Official Video)", TestSupport.DownloadedSongPath()));

        Assert.True(q.IsSentInSong);
        Assert.Equal("Cool Artist", q.Artist);
        Assert.Equal("Nice Song", q.Title);
    }

    [Fact]
    public void Build_RemovesBracketNoise_WhenNoSeparator()
    {
        var q = LyricsQueryNormalizer.Build(
            Track("Cool Artist", "Nice Song (Official Music Video)", TestSupport.DownloadedSongPath()));

        Assert.Equal("Cool Artist", q.Artist);
        Assert.Equal("Nice Song", q.Title);
    }

    [Fact]
    public void Build_RemovesTrailingNoise_AfterPipe()
    {
        var q = LyricsQueryNormalizer.Build(
            Track("Cool Artist", "Nice Song | Official Audio", TestSupport.DownloadedSongPath()));

        Assert.Equal("Nice Song", q.Title);
    }

    [Fact]
    public void Build_NullSong_ReturnsEmpty()
    {
        var q = LyricsQueryNormalizer.Build(null!);

        Assert.Equal(string.Empty, q.Artist);
        Assert.Equal(string.Empty, q.Title);
        Assert.False(q.IsSentInSong);
    }
}
