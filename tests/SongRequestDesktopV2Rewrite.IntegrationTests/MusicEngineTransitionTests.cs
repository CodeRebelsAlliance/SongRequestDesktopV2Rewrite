using System.Collections.Generic;
using System.Threading;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// End-of-track behaviour with a real output: the crossfade must start before the
/// track ends and the queue must advance to the next song.
///
/// The crossfade mixer pads with silence (<c>MixingSampleProvider.ReadFully</c>), so
/// the audio output can never report EOF for a queued track. Advancing therefore
/// depends entirely on the engine's playback tick — if that tick is missing, a song
/// ends, nothing plays next and the queue/UI never updates (the bug this covers).
/// </summary>
public class MusicEngineTransitionTests
{
    [SkippableFact]
    public void TrackEnd_Crossfades_AndAdvancesThroughTheQueue()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        Skip.IfNot(Fixtures.OpenAlAvailable, "OpenAL cannot open an output device here");

        // Normalization off: keeps the test to pure playback (no loudness scan), and
        // an explicit loudness keeps the startup scan from running.
        var engine = new MusicEngine { ConfigAccessor = () => new Config { NormalizeVolume = false } };
        var played = new List<string>();
        var sawCrossfade = false;
        var emptied = new ManualResetEventSlim(false);

        engine.NowPlayingChanged = song =>
        {
            if (song == null) return;
            lock (played) played.Add(song.Title);
        };
        engine.NowPlayingTick += (_, _) => { if (engine.IsCrossfading) sawCrossfade = true; };
        engine.QueueEmptied = () => emptied.Set();

        engine.AddSong(new Song("First", "Test", null, System.TimeSpan.FromSeconds(3), Fixtures.Audio.Mp3) { PerceivedLoudness = -14 });
        engine.AddSong(new Song("Second", "Test", null, System.TimeSpan.FromSeconds(3), Fixtures.Audio.Mp3) { PerceivedLoudness = -14 });
        engine.SetCrossfade(1.0);

        engine.PlaySong(engine.Queue[0]);

        // Both 3s songs, 1s crossfade: the transition lands around t=3s and the queue
        // drains around t=6s. The wide timeout only guards against a hung runner.
        Assert.True(emptied.Wait(30000), "playback never finished the queue");

        Assert.True(sawCrossfade, "the crossfade never started before the track ended");
        lock (played) Assert.Equal("Second", played[^1]);

        var remaining = new List<string>();
        foreach (var song in engine.Queue) remaining.Add(song.Title);
        Assert.Equal(new[] { "Second" }, remaining);
        Assert.False(engine.IsPlaying, "engine still reports playback after the queue drained");
    }
}
