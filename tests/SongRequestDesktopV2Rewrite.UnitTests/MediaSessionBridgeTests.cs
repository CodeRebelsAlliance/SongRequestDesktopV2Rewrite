using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MediaSessionBridgeTests
{
    private static Config NoNormalization() => new Config { NormalizeVolume = false };

    private static Song Track(string title, double seconds = 60)
        => new Song(title, "artist", null, TimeSpan.FromSeconds(seconds), "/nonexistent/" + title + ".mp3");

    private static (MusicEngine Engine, FakeSystemMediaSession Session, MediaSessionBridge Bridge) Create(
        Func<Song?, string?>? artwork = null)
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        var session = new FakeSystemMediaSession();
        var bridge = new MediaSessionBridge(engine, session, artwork);
        return (engine, session, bridge);
    }

    // ── engine → session ───────────────────────────────────────────────────

    [Fact]
    public void Refresh_PublishesMetadataAndStoppedState()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("Hello", 120));

        bridge.Refresh();

        Assert.NotNull(session.Metadata);
        Assert.Equal("Hello", session.Metadata!.Title);
        Assert.Equal("artist", session.Metadata.Artist);
        Assert.Equal(TimeSpan.FromSeconds(120), session.Metadata.Duration);
        Assert.Equal(MediaPlaybackState.Stopped, session.State);

        bridge.Dispose();
    }

    [Fact]
    public void AddSong_PushesMetadataThroughQueueChanged()
    {
        var (engine, session, bridge) = Create();

        engine.AddSong(Track("Queued"));

        Assert.NotNull(session.Metadata);
        Assert.Equal("Queued", session.Metadata!.Title);

        bridge.Dispose();
    }

    [Fact]
    public void Playing_ReportsPlayingState()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("A"));

        var output = new FakeAudioOutput();
        engine.Output = output;
        output.Play();

        bridge.Refresh();

        Assert.Equal(MediaPlaybackState.Playing, session.State);

        output.Pause();
        bridge.Refresh();
        Assert.Equal(MediaPlaybackState.Paused, session.State);

        bridge.Dispose();
    }

    [Fact]
    public void StopPlayback_ReportsStoppedState()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("A"));
        var output = new FakeAudioOutput();
        engine.Output = output;
        output.Play();

        engine.StopPlayback();

        Assert.Equal(MediaPlaybackState.Stopped, session.State);

        bridge.Dispose();
    }

    [Fact]
    public void Refresh_DoesNotResendUnchangedMetadata()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("Stable"));

        int afterAdd = session.MetadataUpdates;
        bridge.Refresh();
        bridge.Refresh();

        Assert.Equal(afterAdd, session.MetadataUpdates);

        bridge.Dispose();
    }

    [Fact]
    public void ArtworkResolver_IsAppliedToMetadata()
    {
        var (engine, session, bridge) = Create(_ => "/tmp/art.png");
        engine.AddSong(Track("WithArt"));

        Assert.Equal("/tmp/art.png", session.Metadata!.ArtworkPath);

        bridge.Dispose();
    }

    [Fact]
    public void Position_IsPublishedFromCurrentReader()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("A"));
        engine.CurrentReader = new FakeAudioSource(frames: 44100) { CurrentTime = TimeSpan.FromSeconds(0.5) };

        bridge.Refresh();

        Assert.Equal(TimeSpan.FromSeconds(0.5), session.Position);

        bridge.Dispose();
    }

    // ── session → engine ───────────────────────────────────────────────────

    [Fact]
    public void NextRequested_ConsumesQueue()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("only"));

        session.RaiseNext();

        Assert.Empty(engine.Queue);

        bridge.Dispose();
    }

    [Fact]
    public void StopRequested_StopsPlayback()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("A"));
        bool emptied = false;
        engine.QueueEmptied = () => emptied = true;

        session.RaiseStop();

        Assert.True(emptied);

        bridge.Dispose();
    }

    [Fact]
    public void VolumeRequested_UpdatesEngineVolume()
    {
        var (engine, session, bridge) = Create();

        session.RaiseVolume(0.42);

        Assert.Equal(0.42f, engine.Volume, 3);

        bridge.Dispose();
    }

    [Fact]
    public void PositionSetRequested_SeeksToAbsolutePosition()
    {
        var (engine, session, bridge) = Create();
        engine.CurrentReader = new FakeAudioSource(frames: 44100); // 1 second long

        session.RaisePositionSet(TimeSpan.FromSeconds(0.25));

        Assert.Equal(0.25, engine.CurrentReader.CurrentTime.TotalSeconds, 3);

        bridge.Dispose();
    }

    [Fact]
    public void SeekRequested_SeeksRelativeToCurrentPosition()
    {
        var (engine, session, bridge) = Create();
        engine.CurrentReader = new FakeAudioSource(frames: 44100) { CurrentTime = TimeSpan.FromSeconds(0.4) };

        session.RaiseSeek(TimeSpan.FromSeconds(0.2));

        Assert.Equal(0.6, engine.CurrentReader.CurrentTime.TotalSeconds, 3);

        bridge.Dispose();
    }

    [Fact]
    public void RaiseAndQuit_AreForwarded()
    {
        var (_, session, bridge) = Create();
        bool raised = false, quit = false;
        bridge.RaiseRequested += (_, _) => raised = true;
        bridge.QuitRequested += (_, _) => quit = true;

        session.RaiseRaise();
        session.RaiseQuit();

        Assert.True(raised);
        Assert.True(quit);

        bridge.Dispose();
    }

    [Fact]
    public void Dispose_DisposesSessionAndDetachesHandlers()
    {
        var (engine, session, bridge) = Create();
        engine.AddSong(Track("A"));
        int updates = session.MetadataUpdates;

        bridge.Dispose();
        engine.AddSong(Track("B"));

        Assert.True(session.Disposed);
        Assert.Equal(updates, session.MetadataUpdates);
    }
}
