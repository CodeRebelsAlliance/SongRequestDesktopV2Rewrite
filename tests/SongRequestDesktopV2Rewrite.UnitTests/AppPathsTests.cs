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
    public void UseDataRoot_PointsEveryWritableFolderAtTheSandbox()
    {
        // A mobile host calls this before anything else: the app bundle is
        // read-only there, so downloads, caches, library and config all have to
        // move under the sandbox root the platform handed us.
        var savedRoot = AppPaths.DataRoot;
        var savedDownload = AppPaths.DownloadPath;
        var savedLibrary = AppPaths.LibraryDataDir;
        var savedAppData = AppPaths.AppDataDir;
        var savedLocalAppData = AppPaths.LocalAppDataDir;

        try
        {
            var root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
            AppPaths.UseDataRoot(root);

            Assert.Equal(root, AppPaths.DataRoot);
            Assert.Equal(Path.Combine(root, "downloadedvideos"), AppPaths.DownloadPath);
            Assert.Equal(Path.Combine(root, "library"), AppPaths.LibraryDataDir);
            Assert.Equal(Path.Combine(root, "downloadedvideos", "thumbnail-cache"), AppPaths.ThumbnailCachePath);
            Assert.Equal(Path.Combine(root, "downloadedvideos", "video-metadata-cache.json"), AppPaths.VideoMetadataCachePath);
            Assert.Equal(root, AppPaths.AppDataDir);
            Assert.Equal(root, AppPaths.LocalAppDataDir);

            // Everything must exist and be writable.
            Assert.True(Directory.Exists(AppPaths.DownloadPath));
            Assert.True(Directory.Exists(AppPaths.LibraryDataDir));
            File.WriteAllText(Path.Combine(AppPaths.LibraryDataDir, "probe.txt"), "ok");
        }
        finally
        {
            AppPaths.DataRoot = savedRoot;
            AppPaths.DownloadPath = savedDownload;
            AppPaths.LibraryDataDir = savedLibrary;
            AppPaths.AppDataDir = savedAppData;
            AppPaths.LocalAppDataDir = savedLocalAppData;
        }
    }

    [Fact]
    public void UseDataRoot_RejectsAnEmptyRoot()
        => Assert.Throws<ArgumentException>(() => AppPaths.UseDataRoot("  "));

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
