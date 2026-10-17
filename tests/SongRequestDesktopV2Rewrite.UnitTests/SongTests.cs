using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SongTests
{
    [Fact]
    public void Constructor_WithDuration_SetsLengthAndDefaults()
    {
        var song = new Song("Title", "Artist", null, TimeSpan.FromSeconds(95), "/x.mp3");

        Assert.Equal("Title", song.Title);
        Assert.Equal("Artist", song.Artist);
        Assert.Equal(TimeSpan.FromSeconds(95), song.Duration);
        Assert.Equal("01:35", song.length);
        Assert.Equal(-1, song.PerceivedLoudness); // not yet analysed
    }

    [Fact]
    public void Constructor_WithLengthString_LeavesDurationZero()
    {
        var song = new Song("Title", "Artist", null, "03:00", "/x.mp3");

        Assert.Equal("03:00", song.length);
        Assert.Equal(TimeSpan.Zero, song.Duration);
    }

    [Fact]
    public void Thumbnail_IsOpaqueObject()
    {
        var payload = new object();
        var song = new Song("T", "A", payload, TimeSpan.Zero, "/x.mp3");
        Assert.Same(payload, song.thumbnail);
    }
}
