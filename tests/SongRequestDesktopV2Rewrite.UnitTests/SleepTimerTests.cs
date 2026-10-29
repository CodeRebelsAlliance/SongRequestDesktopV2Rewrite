using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SleepTimerTests
{
    private static MusicEngine Engine()
    {
        var engine = new MusicEngine
        {
            AutoTick = false,
            ConfigAccessor = () => new Config { NormalizeVolume = false }
        };
        engine.OutputFactory = () => new FakeAudioOutput();
        return engine;
    }

    [Fact]
    public void Start_ExposesRemainingAndPhase_ThenCancelRestoresIdle()
    {
        var engine = Engine();
        var timer = new SleepTimerService(engine) { AutoTick = false };
        var now = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc);
        timer.UtcNow = () => now;

        var started = timer.Start(TimeSpan.FromMinutes(30), fadeOutSeconds: 12, playLastSongToEnd: true);
        Assert.True(started.IsActive);
        Assert.Equal(1800, started.RemainingSeconds, 1);
        Assert.Equal("counting", started.Phase);
        Assert.Equal(12, started.FadeOutSeconds);
        Assert.True(started.PlayLastSongToEnd);

        now = now.AddMinutes(10);
        timer.TickAt(now);
        var state = timer.GetState();
        Assert.Equal(1200, state.RemainingSeconds, 1);

        var cancelled = timer.Cancel();
        Assert.False(cancelled.IsActive);
        Assert.Equal("idle", cancelled.Phase);
    }

    [Fact]
    public void Extend_AddsTime()
    {
        var engine = Engine();
        var timer = new SleepTimerService(engine) { AutoTick = false };
        var now = DateTime.UtcNow;
        timer.UtcNow = () => now;

        timer.Start(TimeSpan.FromMinutes(15));
        var extended = timer.Extend(TimeSpan.FromMinutes(5));

        Assert.Equal(1200, extended.RemainingSeconds, 1);
    }

    [Fact]
    public async Task Expiry_FadesOutAndStopsPlayback()
    {
        var engine = Engine();
        var timer = new SleepTimerService(engine)
        {
            AutoTick = false,
            Delay = _ => Task.CompletedTask
        };
        var now = DateTime.UtcNow;
        timer.UtcNow = () => now;

        engine.StartPlayback(new LongSource(30));
        Assert.True(engine.IsPlaybackActive);
        Assert.NotNull(engine.Output);

        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        timer.Expired += () => expired.TrySetResult(true);

        timer.Start(TimeSpan.FromSeconds(1), fadeOutSeconds: 2, playLastSongToEnd: false);
        now = now.AddSeconds(1.1);
        timer.TickAt(now);

        var done = await Task.WhenAny(expired.Task, Task.Delay(3000));
        Assert.Same(expired.Task, done);

        Assert.False(engine.IsPlaybackActive);
        Assert.Null(engine.Output);
        Assert.Equal(1f, engine.SleepFadeFactor);
        Assert.False(engine.SleepTimerHoldsPlayback);
        Assert.False(timer.GetState().IsActive);
    }

    [Fact]
    public async Task PlayLastSongToEnd_HoldsQueueUntilTheTrackFinishes()
    {
        var engine = Engine();
        var timer = new SleepTimerService(engine)
        {
            AutoTick = false,
            Delay = _ => Task.Delay(5)
        };
        var now = DateTime.UtcNow;
        timer.UtcNow = () => now;

        var source = new LongSource(8);
        engine.StartPlayback(source);

        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        timer.Expired += () => expired.TrySetResult(true);

        timer.Start(TimeSpan.FromSeconds(1), fadeOutSeconds: 1, playLastSongToEnd: true);
        now = now.AddSeconds(1.1);
        timer.TickAt(now);

        // The timer waits for the song instead of stopping it immediately.
        await WaitUntil(() => engine.SleepTimerHoldsPlayback, 2000);
        Assert.True(engine.IsPlaybackActive);
        Assert.Equal("finishing-track", timer.GetState().Phase);

        // Simulate the track reaching its final second, where the fade starts.
        source.CurrentTime = TimeSpan.FromSeconds(7.5);
        var done = await Task.WhenAny(expired.Task, Task.Delay(4000));
        Assert.Same(expired.Task, done);

        Assert.False(engine.IsPlaybackActive);
        Assert.False(engine.SleepTimerHoldsPlayback);
        Assert.Equal(1f, engine.SleepFadeFactor);
    }

    [Fact]
    public void HoldingForLastSong_KeepsPlaybackTicksFlowing()
    {
        var engine = Engine();
        engine.AddSong(new Song("a", "artist", null, TimeSpan.FromSeconds(30), "/nonexistent/held.mp3"));
        engine.StartPlayback(new LongSource(30));
        int ticks = 0;
        engine.NowPlayingTick += (_, _) => ticks++;
        engine.SleepTimerHoldsPlayback = true;

        engine.ProcessTick();

        Assert.True(engine.IsPlaybackActive);
        Assert.True(ticks > 0, "the player must keep receiving ticks while the sleep timer waits for the last song");
    }

    [Fact]
    public void SleepFadeFactor_CombinesWithAnnouncementDucking()
    {
        var engine = Engine();
        var output = new FakeAudioOutput { Volume = 1 };
        engine.OutputFactory = () => output;
        engine.StartPlayback(new LongSource(30));

        engine.AnnouncementOutputFactor = 0.5f;
        engine.SetSleepFadeFactor(0.5f);

        Assert.Equal(0.25f, output.Volume, 3);

        engine.SetSleepFadeFactor(1f);
        Assert.Equal(0.5f, output.Volume, 3);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "condition was not reached before the timeout");
    }

    /// <summary>Source long enough for the sleep timer tests; CurrentTime is settable.</summary>
    private sealed class LongSource : IAudioSource
    {
        private readonly TimeSpan _duration;
        private long _position;

        public LongSource(double seconds)
        {
            _duration = TimeSpan.FromSeconds(seconds);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime => _duration;
        public TimeSpan CurrentTime { get; set; }
        public long Length => 44100L * 2 * 4 * (long)_duration.TotalSeconds;
        public long Position { get => _position; set => _position = value; }
        public float Volume { get; set; } = 1f;

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = 0;
            _position += count;
            return count;
        }

        public ISampleProvider ToSampleProvider() => new Provider(this);
        public void Dispose() { }

        private sealed class Provider : ISampleProvider
        {
            private readonly LongSource _owner;
            public Provider(LongSource owner) => _owner = owner;
            public WaveFormat WaveFormat => _owner.WaveFormat;
            public int Read(float[] buffer, int offset, int count) => _owner.Read(buffer, offset, count);
        }
    }
}
