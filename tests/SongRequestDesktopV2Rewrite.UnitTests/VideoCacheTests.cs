using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class VideoCacheTests
{
    private static string CacheFilePath => Path.GetFullPath(AppPaths.VideoMetadataCachePath);

    [Fact]
    public void GetOrCreate_ReturnsSameInstance()
    {
        var cache = new VideoCache();

        var first = cache.GetOrCreate("abc");
        var second = cache.GetOrCreate("abc");

        Assert.Same(first, second);
        Assert.True(cache.Contains("abc"));
    }

    [Fact]
    public void SaveThenReload_PersistsMetadata()
    {
        if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
        try
        {
            var cache = new VideoCache();
            var data = cache.GetOrCreate("vid1");
            data.Title = "Title";
            data.Creator = "Creator";
            data.DurationTicks = 12345;
            data.IsApproved = true;
            cache.Save();

            var reloaded = new VideoCache();

            Assert.Equal("Title", reloaded.GetCachedTitle("vid1"));
            Assert.Equal("Creator", reloaded.GetCachedCreator("vid1"));
            Assert.Equal(12345, reloaded.GetCachedDurationTicks("vid1"));
            Assert.True(reloaded.TryGet("vid1", out var loaded));
            Assert.True(loaded!.IsApproved);
        }
        finally
        {
            if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
        }
    }

    [Fact]
    public void SetApproved_UpdatesEntry()
    {
        var cache = new VideoCache();
        cache.GetOrCreate("z");

        cache.SetApproved("z", true);

        cache.TryGet("z", out var data);
        Assert.True(data!.IsApproved);
    }

    [Fact]
    public void Remove_DropsEntry()
    {
        var cache = new VideoCache();
        cache.GetOrCreate("x");

        cache.Remove("x");

        Assert.False(cache.Contains("x"));
        Assert.False(cache.TryGet("x", out _));
    }

    [Fact]
    public void Save_RaisesChanged()
    {
        var cache = new VideoCache();
        bool changed = false;
        cache.Changed += () => changed = true;
        cache.GetOrCreate("y");

        cache.Save();

        Assert.True(changed);
    }

    [Fact]
    public void MissingEntry_ReturnsDefaults()
    {
        var cache = new VideoCache();
        Assert.Null(cache.GetCachedTitle("nope"));
        Assert.Equal(0, cache.GetCachedDurationTicks("nope"));
    }
}
