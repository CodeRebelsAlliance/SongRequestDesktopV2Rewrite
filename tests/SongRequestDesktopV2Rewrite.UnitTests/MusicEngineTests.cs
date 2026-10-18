using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MusicEngineTests
{
    private static Config NoNormalization() => new Config { NormalizeVolume = false };

    private static Song Track(string title, double seconds = 60)
        => new Song(title, "artist", null, TimeSpan.FromSeconds(seconds), "/nonexistent/" + title + ".mp3");

    // ── Queue ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddSong_AddsAndRaisesQueueChanged()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        bool changed = false;
        engine.QueueChanged += (_, _) => changed = true;

        engine.AddSong(Track("a"));

        Assert.Single(engine.Queue);
        Assert.True(changed);
    }

    [Fact]
    public void AddSong_Null_IsIgnored()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(null!);
        Assert.Empty(engine.Queue);
    }

    [Fact]
    public void RemoveQueueItem_RemovesByIndex()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("a"));
        engine.AddSong(Track("b"));

        engine.RemoveQueueItem(0);

        Assert.Single(engine.Queue);
        Assert.Equal("b", engine.Queue[0].Title);
    }

    [Fact]
    public void MoveQueueItem_NeverMovesIntoNowPlayingSlot()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("a"));
        engine.AddSong(Track("b"));

        engine.MoveQueueItem(1, 0);

        Assert.Equal("a", engine.Queue[0].Title);
        Assert.Equal("b", engine.Queue[1].Title);
    }

    [Fact]
    public void ShuffleQueue_KeepsNowPlayingFirstAndAllItems()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        for (int i = 0; i < 10; i++) engine.AddSong(Track("s" + i));
        var nowPlaying = engine.Queue[0];

        engine.ShuffleQueue();

        Assert.Same(nowPlaying, engine.Queue[0]);
        Assert.Equal(10, engine.Queue.Count);
    }

    [Fact]
    public void ClearQueue_Empties()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("a"));
        engine.ClearQueue();
        Assert.Empty(engine.Queue);
    }

    // ── Playlist queue ─────────────────────────────────────────────────────

    [Fact]
    public void LoadPlaylist_PreservesOrder_WhenNotShuffled()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.LoadPlaylist(new[] { Track("a"), Track("b"), Track("c") }, shuffle: false);

        Assert.Equal(new[] { "a", "b", "c" }, engine.PlaylistQueue.Select(s => s.Title));
    }

    [Fact]
    public void LoadPlaylist_ShuffleKeepsAllItems()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.LoadPlaylist(new[] { Track("a"), Track("b"), Track("c") }, shuffle: true);
        Assert.Equal(3, engine.PlaylistQueue.Count);
    }

    [Fact]
    public void RefillQueueFromPlaylist_AddsOneWhenQueueShort()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("now"));
        engine.LoadPlaylist(new[] { Track("p1"), Track("p2") }, shuffle: false);

        engine.RefillQueueFromPlaylist();

        Assert.Equal(2, engine.Queue.Count);
        Assert.Equal("p1", engine.Queue[1].Title);
        Assert.Single(engine.PlaylistQueue);
    }

    // ── Crossfade ──────────────────────────────────────────────────────────

    [Fact]
    public void Crossfade_ClampsAndNormalizes()
    {
        var engine = new MusicEngine();

        Assert.Equal(0.4, engine.CrossfadeNormalized, 3);

        engine.SetCrossfade(50);
        Assert.Equal(10, engine.CrossfadeSeconds);
        Assert.Equal(1.0, engine.CrossfadeNormalized, 3);

        engine.AdjustCrossfade(100);
        Assert.Equal(10, engine.CrossfadeSeconds);

        engine.SetCrossfadeFromMidi(0.5);
        Assert.Equal(5, engine.CrossfadeSeconds, 3);

        engine.SetCrossfade(-5);
        Assert.Equal(0, engine.CrossfadeSeconds);
    }

    // ── Volume ─────────────────────────────────────────────────────────────

    [Fact]
    public void SetVolume_ClampsTo0To1()
    {
        var engine = new MusicEngine();
        engine.SetVolume(2f);
        Assert.Equal(1f, engine.Volume);
        engine.SetVolume(-1f);
        Assert.Equal(0f, engine.Volume);
    }

    [Fact]
    public void AdjustVolume_IsBlockedWhenNormalizationEnabled()
    {
        var engine = new MusicEngine { ConfigAccessor = () => new Config { NormalizeVolume = true } };
        engine.Volume = 0.5f;

        engine.AdjustVolume(0.1);

        Assert.Equal(0.5f, engine.Volume);
        Assert.False(engine.CanControlVolume);
    }

    [Fact]
    public void AdjustVolume_RaisesSliderUpdate()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization(), Volume = 0.5f };
        float? slider = null;
        engine.RequestVolumeSliderUpdate = v => slider = v;

        engine.AdjustVolume(0.2);

        Assert.Equal(0.7f, engine.Volume, 3);
        Assert.Equal(0.7f, slider!.Value, 3);
    }

    // ── Normalization ──────────────────────────────────────────────────────

    [Fact]
    public void CalculateNormalizedVolume_AtTargetLoudness_ReturnsDefaultVolume()
    {
        var cfg = new Config { NormalizeVolume = true, NormalizationActive = true, DefaultVolume = 0.8f };
        var engine = new MusicEngine { ConfigAccessor = () => cfg };
        var song = Track("s");
        song.PerceivedLoudness = -14; // == TargetLoudness

        Assert.Equal(0.8f, engine.CalculateNormalizedVolume(song, 0.8f), 3);
    }

    [Fact]
    public void CalculateNormalizedVolume_LouderSong_IsAttenuated()
    {
        var cfg = new Config { NormalizeVolume = true, NormalizationActive = true, DefaultVolume = 0.8f };
        var engine = new MusicEngine { ConfigAccessor = () => cfg };
        var song = Track("s");
        song.PerceivedLoudness = -8;

        var result = engine.CalculateNormalizedVolume(song, 0.8f);

        Assert.True(result < 0.8f && result > 0f, $"expected < 0.8, got {result}");
    }

    [Fact]
    public void CalculateNormalizedVolume_QuieterSong_BoostsAndClamps()
    {
        var cfg = new Config { NormalizeVolume = true, NormalizationActive = true, DefaultVolume = 0.8f };
        var engine = new MusicEngine { ConfigAccessor = () => cfg };
        var song = Track("s");
        song.PerceivedLoudness = -20;

        Assert.Equal(1f, engine.CalculateNormalizedVolume(song, 0.8f), 3);
    }

    [Fact]
    public void CalculateNormalizedVolume_ReturnsBase_WhenDisabled()
    {
        var engine = new MusicEngine { ConfigAccessor = () => new Config { NormalizeVolume = false } };
        var song = Track("s");
        song.PerceivedLoudness = -8;

        Assert.Equal(0.9f, engine.CalculateNormalizedVolume(song, 0.9f), 3);
    }

    [Fact]
    public void CalculateNormalizedVolume_ReturnsBase_ForUnsetOrUnknown()
    {
        var song = Track("s");
        song.PerceivedLoudness = -14;

        // DefaultVolume not set
        var engine = new MusicEngine
        {
            ConfigAccessor = () => new Config { NormalizeVolume = true, NormalizationActive = true, DefaultVolume = -1f }
        };
        Assert.Equal(0.9f, engine.CalculateNormalizedVolume(song, 0.9f), 3);

        // Loudness never analysed (< -50)
        song.PerceivedLoudness = -60;
        engine.ConfigAccessor = () => new Config { NormalizeVolume = true, NormalizationActive = true, DefaultVolume = 0.8f };
        Assert.Equal(0.9f, engine.CalculateNormalizedVolume(song, 0.9f), 3);
    }

    // ── Queue timings / misc ───────────────────────────────────────────────

    [Fact]
    public void ComputeQueueTimings_SetsPlayingNowAndWaitTimes()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("a", 60));
        engine.AddSong(Track("b", 120));
        engine.AddSong(Track("c", 30));

        engine.ComputeQueueTimings();

        Assert.Equal("Playing Now", engine.Queue[0].EstimatedStartDisplay);
        Assert.Equal(TimeSpan.Zero, engine.Queue[1].EstimatedStart);
        // Without a current reader the wait time is the sum of the durations of the
        // preceding queued items (the now-playing track is already included).
        Assert.Equal(TimeSpan.FromSeconds(120), engine.Queue[2].EstimatedStart);
        Assert.Equal("Wait Time: 02:00", engine.Queue[2].EstimatedStartDisplay);
    }

    /// <summary>
    /// The queue notification is what the music player window renders, so the wait
    /// times must be recomputed *before* it is raised. They used to be computed after
    /// the ObservableCollection's CollectionChanged event (which raises the
    /// notification), so a freshly added item was announced with its stale default
    /// "00:00" and only looked right after some later refresh.
    /// </summary>
    [Fact]
    public void QueueChanged_AnnouncesFreshWaitTimes_ForTheNewlyAddedItem()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        var announced = new List<string[]>();
        engine.QueueChanged += (_, _)
            => announced.Add(engine.Queue.Select(s => s.EstimatedStartDisplay).ToArray());

        engine.AddSong(Track("a", 60));
        Assert.Equal(new[] { "Playing Now" }, announced[^1]);

        engine.AddSong(Track("b", 60));
        Assert.Equal(new[] { "Playing Now", "Wait Time: Now" }, announced[^1]);

        // The third item's own wait time has to be in the very announcement that adds
        // it — this is the assertion that fails when the timings are computed last.
        engine.AddSong(Track("c", 60));
        Assert.Equal(new[] { "Playing Now", "Wait Time: Now", "Wait Time: 01:00" }, announced[^1]);
    }

    [Fact]
    public void QueueChanged_RecalculatesWaitTimes_WhenAnItemIsRemoved()
    {
        var engine = new MusicEngine { ConfigAccessor = () => NoNormalization() };
        engine.AddSong(Track("a", 60));
        engine.AddSong(Track("b", 120));
        engine.AddSong(Track("c", 60));

        var announced = Array.Empty<string>();
        engine.QueueChanged += (_, _)
            => announced = engine.Queue.Select(s => s.EstimatedStartDisplay).ToArray();

        engine.RemoveQueueItem(1);   // drop the 2 minute track

        // "c" now follows "a" alone, so its wait time shrinks to zero.
        Assert.Equal(new[] { "Playing Now", "Wait Time: Now" }, announced);
    }

    [Fact]
    public void ToggleRepeat_Toggles()
    {
        var engine = new MusicEngine();
        Assert.False(engine.RepeatEnabled);
        engine.ToggleRepeat();
        Assert.True(engine.RepeatEnabled);
    }

    [Fact]
    public void IsPlaying_FalseWhenNoOutput()
    {
        var engine = new MusicEngine();
        Assert.False(engine.IsPlaying);
        Assert.False(engine.IsPlaybackActive);
    }
}
