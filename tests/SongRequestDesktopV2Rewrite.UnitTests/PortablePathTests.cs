using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Library paths must survive a moving sandbox: mobile app containers change
/// between installs (iOS) and the app's own storage is the only place it can
/// always read. Files under the data root are therefore stored as <c>~/…</c> and
/// resolved on load, while the user's own folders stay absolute.
/// </summary>
public class PortablePathTests
{
    [Fact]
    public void ToPortablePath_RelativisesPathsUnderTheDataRoot()
    {
        var saved = AppPaths.DataRoot;
        try
        {
            var root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
            AppPaths.DataRoot = root;

            var inside = Path.Combine(root, "music", "album", "song.mp3");
            Assert.Equal("~/music/album/song.mp3", AppPaths.ToPortablePath(inside));

            // A file directly under the root, and one nested deeper.
            Assert.Equal("~/song.mp3", AppPaths.ToPortablePath(Path.Combine(root, "song.mp3")));
        }
        finally
        {
            AppPaths.DataRoot = saved;
        }
    }

    [Fact]
    public void ToPortablePath_LeavesForeignAndRelativePathsAlone()
    {
        var saved = AppPaths.DataRoot;
        try
        {
            var root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
            AppPaths.DataRoot = root;

            // The user's own music folder is theirs: never rewritten.
            Assert.Equal("/home/someone/Music/song.mp3", AppPaths.ToPortablePath("/home/someone/Music/song.mp3"));

            // Already portable, or a bare relative path: unchanged.
            Assert.Equal("~/music/song.mp3", AppPaths.ToPortablePath("~/music/song.mp3"));
            Assert.Equal("data/downloadedvideos/x.mp3", AppPaths.ToPortablePath("data/downloadedvideos/x.mp3"));
        }
        finally
        {
            AppPaths.DataRoot = saved;
        }
    }

    [Fact]
    public void ResolvePortablePath_IsTheInverse()
    {
        var saved = AppPaths.DataRoot;
        try
        {
            var root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
            AppPaths.DataRoot = root;

            var resolved = AppPaths.ResolvePortablePath("~/music/album/song.mp3");
            Assert.Equal(Path.Combine(root, "music", "album", "song.mp3"), resolved);

            // Absolute paths pass through untouched.
            Assert.Equal("/home/someone/Music/song.mp3", AppPaths.ResolvePortablePath("/home/someone/Music/song.mp3"));

            // Round trip.
            Assert.Equal(resolved, AppPaths.ResolvePortablePath(AppPaths.ToPortablePath(resolved)));
        }
        finally
        {
            AppPaths.DataRoot = saved;
        }
    }

    [Fact]
    public void ResolvePortablePath_FollowsANewRootAfterTheSandboxMoved()
    {
        var saved = AppPaths.DataRoot;
        try
        {
            var first = Path.Combine(TestSupport.NewTempDir(), "container-a");
            AppPaths.DataRoot = first;
            var portable = AppPaths.ToPortablePath(Path.Combine(first, "music", "song.mp3"));
            Assert.Equal("~/music/song.mp3", portable);

            // Same stored string, new container (what an iOS reinstall looks like).
            var second = Path.Combine(TestSupport.NewTempDir(), "container-b");
            AppPaths.DataRoot = second;
            Assert.Equal(Path.Combine(second, "music", "song.mp3"), AppPaths.ResolvePortablePath(portable));
        }
        finally
        {
            AppPaths.DataRoot = saved;
        }
    }
}
