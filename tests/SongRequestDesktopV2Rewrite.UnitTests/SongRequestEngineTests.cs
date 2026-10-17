using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SongRequestEngineTests
{
    [Fact]
    public void NewEngine_HasSensibleDefaults()
    {
        var engine = new SongRequestEngine();

        Assert.NotNull(engine.Cache);
        Assert.Empty(engine.FetchedYtids);
        Assert.Empty(engine.FetchedBlacklist);
        Assert.Empty(engine.ProcessingVideos);
        Assert.False(engine.InitialLoadComplete);
        Assert.True(engine.DownloadSemaphore.CurrentCount >= 1);
        Assert.Equal(AppPaths.DownloadPath, SongRequestEngine.DownloadPath);
    }

    [Fact]
    public void ProcessingVideos_IsCaseInsensitive()
    {
        var engine = new SongRequestEngine();
        engine.ProcessingVideos.TryAdd("AbC", 1);
        Assert.True(engine.ProcessingVideos.ContainsKey("abc"));
    }
}
