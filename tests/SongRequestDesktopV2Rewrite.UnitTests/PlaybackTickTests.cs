using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The engine drives its own <see cref="MusicEngine.ProcessTick"/> while something is
/// playing — that is what feeds the music player's elapsed time and progress bar, and
/// what notices the end of a track (the crossfade mixer pads with silence, so the
/// audio output can never report EOF).
/// </summary>
public class PlaybackTickTests
{
    [Fact]
    public async Task PlaybackTick_RaisesNowPlayingUpdates_WithTheCurrentPosition()
    {
        var host = new HeadlessHost(new List<Cookie>());
        try
        {
            var output = new FakeAudioOutput();
            host.Music.OutputFactory = () => output;
            host.Music.AddSong(new Song("Title", "Artist", null, TimeSpan.FromSeconds(1), "/nonexistent/song.mp3"));

            var tick = new TaskCompletionSource<NowPlayingEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = false;
            host.Music.NowPlayingTick += (_, args) =>
            {
                // Ignore the update StartPlayback raises synchronously; only a tick from
                // the engine's timer should satisfy this test.
                if (started && args.Current != null) tick.TrySetResult(args);
            };

            host.Music.StartPlayback(new FakeAudioSource(frames: 44100) { CurrentTime = TimeSpan.FromSeconds(0.25) });
            started = true;

            var completed = await Task.WhenAny(tick.Task, Task.Delay(5000));

            Assert.True(ReferenceEquals(tick.Task, completed), "no playback tick arrived within 5s");
            var args = await tick.Task;
            Assert.NotNull(args.Current);
            Assert.Equal("Title", args.Current!.Title);
            Assert.Equal(0.25, args.CurrentTime.TotalSeconds, 2);
            Assert.Equal(1.0, args.TotalTime.TotalSeconds, 2);
        }
        finally
        {
            try { host.Music.StopPlayback(); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTick_StopsWhenPlaybackStops()
    {
        var host = new HeadlessHost(new List<Cookie>());
        try
        {
            var output = new FakeAudioOutput();
            host.Music.OutputFactory = () => output;
            host.Music.AddSong(new Song("Title", "Artist", null, TimeSpan.FromSeconds(1), "/nonexistent/song.mp3"));

            int ticks = 0;
            host.Music.NowPlayingTick += (_, _) => System.Threading.Interlocked.Increment(ref ticks);

            host.Music.StartPlayback(new FakeAudioSource(frames: 44100));
            await Task.Delay(400);
            Assert.True(ticks > 0, "expected playback ticks while playing");

            host.Music.StopPlayback();
            var after = ticks;
            await Task.Delay(400);
            Assert.Equal(after, ticks);
        }
        finally
        {
            try { host.Music.StopPlayback(); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackTick_IsSilent_WhenNothingIsPlaying()
    {
        var host = new HeadlessHost(new List<Cookie>());
        host.Music.AddSong(new Song("Title", "Artist", null, TimeSpan.FromSeconds(1), "/nonexistent/song.mp3"));

        var ticked = false;
        host.Music.NowPlayingTick += (_, _) => ticked = true;

        await Task.Delay(400);

        Assert.False(ticked);
    }
}
