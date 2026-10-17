using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MusicShareAudioPlayerTests
{
    private static string Chunk(int bytes = 35280) => Convert.ToBase64String(new byte[bytes]);

    [Fact]
    public void Feed_WarmsUpAtBufferLevelThree_ThenPlays()
    {
        var output = new FakeAudioOutput();
        using var player = new MusicShareAudioPlayer { OutputFactory = () => output };
        player.Start();

        player.Feed(Chunk(), bufferLevel: 1);
        Assert.Equal(0, output.PlayCount); // not warmed up yet

        player.Feed(Chunk(), bufferLevel: 3);
        Assert.Equal(1, output.PlayCount);

        // Already playing: no further Play calls
        player.Feed(Chunk(), bufferLevel: 5);
        Assert.Equal(1, output.PlayCount);
    }

    [Fact]
    public void Volume_IsClampedTo0To1()
    {
        var player = new MusicShareAudioPlayer();

        player.Volume = 2f;
        Assert.Equal(1f, player.Volume);

        player.Volume = -1f;
        Assert.Equal(0f, player.Volume);
    }

    [Fact]
    public void Volume_IsForwardedToOutput()
    {
        var output = new FakeAudioOutput();
        using var player = new MusicShareAudioPlayer { OutputFactory = () => output };
        player.Start();

        player.Volume = 0.5f;

        Assert.Equal(0.5f, output.Volume);
    }

    [Fact]
    public void Feed_WithoutStart_IsNoOp()
    {
        using var player = new MusicShareAudioPlayer();
        player.Feed(Chunk(64), bufferLevel: 5); // must not throw
    }

    [Fact]
    public void Stop_IsSafeToCallRepeatedly()
    {
        var output = new FakeAudioOutput();
        var player = new MusicShareAudioPlayer { OutputFactory = () => output };
        player.Start();

        player.Stop();
        player.Stop();

        Assert.True(output.StopCount >= 1);
    }
}
