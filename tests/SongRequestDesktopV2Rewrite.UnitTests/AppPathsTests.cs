using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class AppPathsTests
{
    [Fact]
    public void AppDataDir_IsSettable_AndCreatesDirectory()
    {
        var original = AppPaths.AppDataDir;
        try
        {
            var dir = Path.Combine(TestSupport.NewTempDir(), "nested");
            AppPaths.AppDataDir = dir;

            Assert.True(Directory.Exists(dir));
            Assert.Equal(Path.Combine(dir, "youtube_auth.json"), AppPaths.YoutubeAuthPath);
        }
        finally
        {
            AppPaths.AppDataDir = original;
        }
    }

    [Fact]
    public void DownloadPaths_AreStableAndNested()
    {
        Assert.Equal(Path.Combine("data", "downloadedvideos"), AppPaths.DownloadPath);
        Assert.Equal(Path.Combine(AppPaths.DownloadPath, "thumbnail-cache"), AppPaths.ThumbnailCachePath);
        Assert.Equal(Path.Combine(AppPaths.DownloadPath, "video-metadata-cache.json"), AppPaths.VideoMetadataCachePath);
    }

    [Fact]
    public void BaseDirectory_IsNotEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppPaths.BaseDirectory));

    [Fact]
    public void ToolsDirectories_AreNestedCorrectly()
    {
        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "tools"), AppPaths.ToolsDirectory);
        Assert.Equal(Path.Combine(AppPaths.AppDataDir, "tools"), AppPaths.UserToolsDirectory);
    }
}
