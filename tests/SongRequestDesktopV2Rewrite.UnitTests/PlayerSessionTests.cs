using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class PlayerSessionTests
{
    private static Config NoNormalization() => new Config { NormalizeVolume = false };

    private static string Sample(string name = "song")
    {
        var source = Path.Combine(AppContext.BaseDirectory, "announcement.mp3");
        var path = Path.Combine(TestSupport.NewTempDir("player-session"), name + ".mp3");
        File.Copy(source, path);
        return path;
    }

    private static PlayerSessionSong SessionSong(string path, string title = "Song")
        => new() { SongPath = path, Title = title, Artist = "Artist", DurationSeconds = 1.8 };

    [Fact]
    public void Store_RoundTripsAndHandlesCorruptFiles()
    {
        var path = Path.Combine(TestSupport.NewTempDir("session-store"), "session.json");
        var store = new PlayerSessionStore(path);
        var state = new PlayerSessionState
        {
            CurrentSongPath = "/music/a.mp3",
            PositionSeconds = 12.5,
            Queue = { SessionSong("/music/a.mp3", "A") },
            History = { SessionSong("/music/old.mp3", "Old") },
        };

        Assert.True(store.Save(state));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("/music/a.mp3", loaded!.CurrentSongPath);
        Assert.Equal(12.5, loaded.PositionSeconds, 2);
        Assert.Single(loaded.Queue);
        Assert.Single(loaded.History);

        File.WriteAllText(path, "this is not json");
        Assert.Null(store.Load());

        File.WriteAllText(path, "{\"Version\":999}");
        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_MissingFile_ReturnsNull()
    {
        var store = new PlayerSessionStore(Path.Combine(TestSupport.NewTempDir("session-missing"), "none.json"));
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Restore_AppliesQueueTrackPositionVolumeAndHistory_Paused()
    {
        var a = Sample("a");
        var b = Sample("b");
        var old = Sample("old");
        var storePath = Path.Combine(TestSupport.NewTempDir("session-restore"), "session.json");
        var store = new PlayerSessionStore(storePath);
        store.Save(new PlayerSessionState
        {
            CurrentSongPath = a,
            PositionSeconds = 0.5,
            Volume = 0.42,
            CrossfadeSeconds = 6,
            OutputDeviceNumber = 3,
            RepeatEnabled = true,
            Queue = { SessionSong(a, "A"), SessionSong(b, "B") },
            PlaylistQueue = { SessionSong(b, "B") },
            History = { SessionSong(old, "Old") },
        });

        var engine = new MusicEngine
        {
            ConfigAccessor = () => NoNormalization(),
            OutputFactory = () => new FakeAudioOutput(),
        };
        using var service = new PlayerSessionService(engine, store);

        Assert.True(await service.RestoreAsync());

        Assert.Equal(2, engine.Queue.Count);
        Assert.Single(engine.PlaylistQueue);
        Assert.Single(engine.GetPlaybackHistory());
        Assert.Equal("A", engine.Queue[0].Title);
        Assert.Equal(0.42, engine.Volume, 2);
        Assert.Equal(6, engine.CrossfadeSeconds, 2);
        Assert.Equal(3, engine.OutputDeviceNumber);
        Assert.True(engine.RepeatEnabled);
        Assert.NotNull(engine.CurrentReader);
        Assert.False(engine.IsPlaying);
        Assert.InRange(engine.CurrentPosition.TotalSeconds, 0.35, 0.65);
        Assert.Equal(0, ((FakeAudioOutput)engine.Output!).PlayCount);
    }

    [Fact]
    public async Task Restore_MissingFilesArePruned_AndDoesNotThrow()
    {
        var storePath = Path.Combine(TestSupport.NewTempDir("session-missing-restore"), "session.json");
        var store = new PlayerSessionStore(storePath);
        store.Save(new PlayerSessionState
        {
            CurrentSongPath = "/definitely/missing/a.mp3",
            Queue = { SessionSong("/definitely/missing/a.mp3"), SessionSong("/definitely/missing/b.mp3") },
            History = { SessionSong("/definitely/missing/old.mp3") },
        });

        var engine = new MusicEngine
        {
            ConfigAccessor = () => NoNormalization(),
            OutputFactory = () => new FakeAudioOutput(),
        };
        using var service = new PlayerSessionService(engine, store);

        Assert.True(await service.RestoreAsync());
        Assert.Empty(engine.Queue);
        Assert.Empty(engine.GetPlaybackHistory());
        Assert.Null(engine.CurrentReader);
    }

    [Fact]
    public async Task Restore_CorruptSession_LeavesEngineUsable()
    {
        var storePath = Path.Combine(TestSupport.NewTempDir("session-corrupt"), "session.json");
        File.WriteAllText(storePath, "garbage");
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        using var service = new PlayerSessionService(engine, new PlayerSessionStore(storePath));

        Assert.False(await service.RestoreAsync());
        Assert.True(service.RestoreCompleted);
        Assert.Empty(engine.Queue);
    }

    [Fact]
    public void Capture_WritesCurrentTrackAndProgress()
    {
        var a = Sample("capture");
        var storePath = Path.Combine(TestSupport.NewTempDir("session-capture"), "session.json");
        var store = new PlayerSessionStore(storePath);
        var engine = new MusicEngine
        {
            ConfigAccessor = () => NoNormalization(),
            OutputFactory = () => new FakeAudioOutput(),
        };
        engine.AddSong(new Song("A", "Artist", null, TimeSpan.FromSeconds(1), a));
        var source = new FakeAudioSource(frames: 44100) { CurrentTime = TimeSpan.FromSeconds(0.4) };
        engine.StartPlayback(source);
        using var service = new PlayerSessionService(engine, store);

        Assert.True(service.Flush());

        var saved = store.Load();
        Assert.NotNull(saved);
        Assert.Equal(a, saved!.CurrentSongPath);
        Assert.InRange(saved.PositionSeconds, 0.35, 0.45);
        Assert.Single(saved.Queue);
    }

    [Fact]
    public void PlaybackHistory_KeepsTheLastThreeTracks()
    {
        var a = Sample("h1");
        var b = Sample("h2");
        var c = Sample("h3");
        var d = Sample("h4");
        var engine = new MusicEngine
        {
            ConfigAccessor = () => NoNormalization(),
            OutputFactory = () => new FakeAudioOutput(),
        };

        engine.AddSong(new Song("A", "Artist", null, TimeSpan.FromSeconds(1), a));
        engine.StartPlayback(new FakeAudioSource(frames: 44100));
        engine.Queue[0] = new Song("B", "Artist", null, TimeSpan.FromSeconds(1), b);
        engine.StartPlayback(new FakeAudioSource(frames: 44100));
        engine.Queue[0] = new Song("C", "Artist", null, TimeSpan.FromSeconds(1), c);
        engine.StartPlayback(new FakeAudioSource(frames: 44100));
        engine.Queue[0] = new Song("D", "Artist", null, TimeSpan.FromSeconds(1), d);
        engine.StartPlayback(new FakeAudioSource(frames: 44100));

        var history = engine.GetPlaybackHistory();
        Assert.Equal(new[] { "A", "B", "C" }, history.Select(s => s.Title));
    }

    [Fact]
    public async Task Previous_GoesBackThroughHistory_AndRestartsPastThreeSeconds()
    {
        var a = Sample("p1");
        var b = Sample("p2");
        var engine = new MusicEngine
        {
            ConfigAccessor = () => NoNormalization(),
            OutputFactory = () => new FakeAudioOutput(),
        };

        engine.AddSong(new Song("A", "Artist", null, TimeSpan.FromSeconds(1), a));
        engine.StartPlayback(new FakeAudioSource(frames: 44100));
        engine.Queue[0] = new Song("B", "Artist", null, TimeSpan.FromSeconds(1), b);
        engine.StartPlayback(new FakeAudioSource(frames: 44100));

        // Past three seconds: Previous restarts, history stays.
        engine.CurrentReader!.CurrentTime = TimeSpan.FromSeconds(5);
        engine.Previous();
        Assert.True(engine.CurrentPosition < TimeSpan.FromSeconds(0.1));
        Assert.Single(engine.GetPlaybackHistory());

        // At the beginning: Previous pops the history entry into the queue.
        engine.CurrentReader!.CurrentTime = TimeSpan.Zero;
        engine.Previous();
        Assert.Equal("A", engine.Queue[0].Title);
        Assert.Empty(engine.GetPlaybackHistory());

        for (int i = 0; i < 100 && (engine.CurrentSong == null || engine.CurrentSong.songPath != a); i++)
            await Task.Delay(10);
        Assert.Equal(a, engine.CurrentSong?.songPath);
    }

    [Fact]
    public void RestorePlaybackHistory_ClampsToThreeAndDeduplicates()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.RestorePlaybackHistory(new[]
        {
            new Song("A", "Artist", null, TimeSpan.FromSeconds(1), "/a.mp3"),
            new Song("A", "Artist", null, TimeSpan.FromSeconds(1), "/a.mp3"),
            new Song("B", "Artist", null, TimeSpan.FromSeconds(1), "/b.mp3"),
            new Song("C", "Artist", null, TimeSpan.FromSeconds(1), "/c.mp3"),
            new Song("D", "Artist", null, TimeSpan.FromSeconds(1), "/d.mp3"),
        });

        Assert.Equal(new[] { "A", "B", "C" }, engine.GetPlaybackHistory().Select(s => s.Title));
    }
}
