using System;
using System.IO;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MediaArtworkTests
{
    private static Song Track(string path = "/music/song.mp3", object? thumbnail = null)
        => new Song("Title", "Artist", thumbnail, TimeSpan.FromSeconds(30), path);

    [Fact]
    public void Resolve_NullSong_ReturnsNull()
    {
        Assert.Null(MediaArtwork.Resolve(null, TestSupport.NewTempDir()));
    }

    [Fact]
    public void Resolve_PrefersSongThumbnailDataUrl()
    {
        var dir = TestSupport.NewTempDir("artwork");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var song = Track("/music/local.mp3", "data:image/jpeg;base64," + Convert.ToBase64String(bytes));

        var path = MediaArtwork.Resolve(song, dir);

        Assert.NotNull(path);
        Assert.Equal(dir, Path.GetDirectoryName(path!));
        Assert.EndsWith(".jpg", path, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path!));
    }

    [Fact]
    public void WriteDataUrl_DecodesPng()
    {
        var dir = TestSupport.NewTempDir("artwork");
        var bytes = new byte[] { 9, 8, 7 };
        var path = MediaArtwork.WriteDataUrl("data:image/png;base64," + Convert.ToBase64String(bytes), dir, "cover");

        Assert.NotNull(path);
        Assert.EndsWith(".png", path, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path!));
    }

    [Fact]
    public void WriteDataUrl_AcceptsBareBase64()
    {
        var dir = TestSupport.NewTempDir("artwork");
        var path = MediaArtwork.WriteDataUrl(Convert.ToBase64String(new byte[] { 1, 2 }), dir, "bare");

        Assert.NotNull(path);
        Assert.EndsWith(".jpg", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!!")]
    [InlineData("data:image/png;base64,")]
    public void WriteDataUrl_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(MediaArtwork.WriteDataUrl(input, TestSupport.NewTempDir("artwork"), "x"));
    }

    [Fact]
    public void Resolve_FallsBackToYouTubeThumbnailCache()
    {
        var dir = TestSupport.NewTempDir("artwork");
        var videoId = "v" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(AppPaths.ThumbnailCachePath);
        var cached = Path.Combine(AppPaths.ThumbnailCachePath, videoId + ".png");
        File.WriteAllBytes(cached, new byte[] { 5 });

        try
        {
            var resolved = MediaArtwork.Resolve(Track(videoId + ".mp3"), dir);
            Assert.Equal(cached, resolved);
        }
        finally
        {
            File.Delete(cached);
        }
    }

    [Fact]
    public void GetVideoId_UsesFileNameWithoutExtension()
    {
        Assert.Equal("abc", MediaArtwork.GetVideoId("/downloads/abc.mp3"));
        Assert.Equal("abc", MediaArtwork.GetVideoId("abc"));
        Assert.Null(MediaArtwork.GetVideoId(null));
    }

    [Fact]
    public void StableKey_IsDeterministicAndFileSystemSafe()
    {
        var song = Track("/music/a.mp3");
        var other = Track("/music/a.mp3");
        var different = Track("/music/b.mp3");

        var key = MediaArtwork.StableKey(song);

        Assert.Equal(key, MediaArtwork.StableKey(other));
        Assert.NotEqual(key, MediaArtwork.StableKey(different));
        Assert.DoesNotContain("/", key);
        Assert.DoesNotContain("\\", key);
        Assert.Equal(16, key.Length);
    }
}
