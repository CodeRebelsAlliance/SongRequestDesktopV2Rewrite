using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Exercises the OpenAL Soft output. OpenAL runs with the null backend (see
/// tests/coverage.runsettings), so these tests pass without a sound card; if a
/// machine/runner cannot open an OpenAL device at all they skip instead of
/// failing the build.
/// </summary>
public class OpenAlAudioOutputTests
{
    private static void RequireOpenAl()
        => Skip.IfNot(Fixtures.OpenAlAvailable, "OpenAL cannot open an output device here");

    [SkippableFact]
    public void Constructs_WithNullDriver()
    {
        RequireOpenAl();

        using var output = new OpenAlAudioOutput();
        Assert.Equal(PlaybackState.Stopped, output.PlaybackState);
    }

    [SkippableFact]
    public void PlaysSyntheticStream_ToCompletion_AndRaisesStopped()
    {
        RequireOpenAl();

        long total = 44100L * 2; // 1 s stereo
        var provider = new CountProvider(44100, 2, total);
        using var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();

        output.Init(provider);
        output.Volume = 0.7f;
        output.Play();

        Assert.Equal(PlaybackState.Playing, output.PlaybackState);
        Assert.True(stopped.Wait(8000), "PlaybackStopped did not fire at EOF");
        Assert.Equal(total, provider.TotalRead);
        Assert.Equal(PlaybackState.Stopped, output.PlaybackState);
    }

    [SkippableFact]
    public void PauseResume_AndStop_ChangeState()
    {
        RequireOpenAl();

        var provider = new CountProvider(44100, 2, 44100L * 2 * 5);
        using var output = new OpenAlAudioOutput();
        output.Init(provider);
        output.Play();

        output.Pause();
        Assert.Equal(PlaybackState.Paused, output.PlaybackState);

        output.Play();
        Assert.Equal(PlaybackState.Playing, output.PlaybackState);

        output.Stop();
        Assert.Equal(PlaybackState.Stopped, output.PlaybackState);
    }

    [SkippableFact]
    public void ExplicitStop_RaisesStopped()
    {
        RequireOpenAl();

        var provider = new CountProvider(44100, 2, 44100L * 2 * 5);
        using var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();
        output.Init(provider);
        output.Play();

        output.Stop();

        Assert.True(stopped.Wait(3000));
    }

    [SkippableFact]
    public void EnumeratesAtLeastOneDevice_AndPlaysOnSelectedDevice()
    {
        RequireOpenAl();

        var devices = AudioDevices.Enumerate();
        Assert.NotEmpty(devices);

        var provider = new CountProvider(44100, 2, 44100L * 2);
        using var output = new OpenAlAudioOutput { DeviceNumber = 0 };
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();

        output.Init(provider);
        output.Play();

        Assert.True(stopped.Wait(8000));
    }

    [SkippableFact]
    public void BufferedProvider_ReadFullyTrue_KeepsPlaying()
    {
        RequireOpenAl();

        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
        };
        using var output = new OpenAlAudioOutput();
        output.Init((IWaveProvider)buffer);
        int n = 44100 * 2 * 4 / 2;
        buffer.AddSamples(new byte[n], 0, n);

        output.Play();
        Thread.Sleep(400);

        Assert.Equal(PlaybackState.Playing, output.PlaybackState);
    }

    [SkippableFact]
    public void BufferedProvider_ReadFullyFalse_StarvesAndStops()
    {
        RequireOpenAl();

        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            ReadFully = false,
            DiscardOnBufferOverflow = true,
        };
        using var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();
        output.Init((IWaveProvider)buffer);
        int n = 44100 * 2 * 4 / 4;
        buffer.AddSamples(new byte[n], 0, n);

        output.Play();

        Assert.True(stopped.Wait(5000));
    }

    [SkippableFact]
    public void ConcurrentOutputs_AllComplete()
    {
        RequireOpenAl();

        long total = 44100L * 2 / 4; // 0.25 s per stream keeps this fast under CI load
        var outputs = new List<OpenAlAudioOutput>();
        var providers = new List<CountProvider>();
        var events = new List<ManualResetEventSlim>();

        for (int i = 0; i < 4; i++)
        {
            var provider = new CountProvider(44100, 2, total);
            var output = new OpenAlAudioOutput();
            var stopped = new ManualResetEventSlim(false);
            output.PlaybackStopped += (_, _) => stopped.Set();
            output.Init(provider);
            output.Play();
            outputs.Add(output);
            providers.Add(provider);
            events.Add(stopped);
        }

        try
        {
            for (int i = 0; i < events.Count; i++)
            {
                bool finished = events[i].Wait(20000);
                Assert.True(finished, $"output {i} stalled: state={outputs[i].PlaybackState}, read={providers[i].TotalRead}/{total}");
            }
            Assert.All(providers, p => Assert.Equal(total, p.TotalRead));
        }
        finally
        {
            foreach (var output in outputs) output.Dispose();
        }
    }

    /// <summary>
    /// OpenAL's current context is process-wide with the build we ship (not per-thread),
    /// and the app runs two outputs at once: the music and the announcement chime. Every
    /// AL call therefore has to be serialized and must re-select its own context, or the
    /// other output's context is the current one while a feed loop uploads its buffers —
    /// each call then fails with AL_INVALID_NAME, the output stops being fed and the
    /// track "hangs" (the reported bug).
    ///
    /// The provider switches to a foreign context from inside <c>Read</c>, i.e. in the
    /// middle of the feed loop's AL sequence (exactly the worst-case interleaving, made
    /// deterministic here instead of relying on timing). With the fix the output simply
    /// re-selects its context and keeps playing.
    /// </summary>
    [SkippableFact]
    public async Task ForeignCurrentContext_DoesNotBreakAPlayingOutput()
    {
        RequireOpenAl();

        using var foreign = new ForeignContext();
        Skip.IfNot(foreign.IsValid, "OpenAL cannot open a device here");

        var provider = new SlowEndlessProvider(44100, 2, 440, decodeDelayMs: 0);
        provider.OnRead = foreign.MakeCurrent;   // "another output's thread switched context"

        var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();

        try
        {
            output.Init(provider);
            output.Play();

            long before = provider.TotalRead;
            await Task.Delay(2000);
            long fed = provider.TotalRead - before;

            Assert.False(stopped.IsSet, "the output was reported as stopped while audio was still available");
            Assert.True(fed >= 44100 * 2 * 1.2,
                $"the output stopped being fed after a foreign context was made current: {fed / (44100.0 * 2):0.00}s of audio in 2s");
        }
        finally
        {
            try { output.Dispose(); } catch { }
        }
    }

    /// <summary>A context owned by the test, used to make another context current.</summary>
    private sealed unsafe class ForeignContext : IDisposable
    {
        private readonly Silk.NET.OpenAL.ALContext _alc;
        private readonly Silk.NET.OpenAL.Device* _device;
        private readonly Silk.NET.OpenAL.Context* _context;

        public ForeignContext()
        {
            _alc = Silk.NET.OpenAL.ALContext.GetApi();
            _device = _alc.OpenDevice(null);
            if (_device != null) _context = _alc.CreateContext(_device, null);
        }

        public bool IsValid => _device != null && _context != null;

        public void MakeCurrent()
        {
            if (_context == null) return;
            lock (OpenAlAudioOutput.AlGate) { try { _alc.MakeContextCurrent(_context); } catch { } }
        }

        public void Dispose()
        {
            if (_context != null)
            {
                lock (OpenAlAudioOutput.AlGate)
                {
                    try { _alc.MakeContextCurrent(_context); } catch { }
                    try { _alc.DestroyContext(_context); } catch { }
                }
            }
            if (_device != null) { try { _alc.CloseDevice(_device); } catch { } }
        }
    }

    /// <summary>Endless tone whose <see cref="Read"/> takes a moment, like a real decode.</summary>
    private sealed class SlowEndlessProvider : NAudio.Wave.ISampleProvider
    {
        private readonly double _freq;
        private readonly int _delayMs;
        private double _phase;
        private long _read;

        public SlowEndlessProvider(int sampleRate, int channels, double freq, int decodeDelayMs)
        {
            WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _freq = freq;
            _delayMs = decodeDelayMs;
        }

        /// <summary>Runs inside <see cref="Read"/>, i.e. between the feed loop's AL calls.</summary>
        public Action? OnRead { get; set; }

        public NAudio.Wave.WaveFormat WaveFormat { get; }
        public long TotalRead => System.Threading.Interlocked.Read(ref _read);

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += 2)
            {
                float v = (float)(0.3 * Math.Sin(_phase));
                _phase += 2 * Math.PI * _freq / WaveFormat.SampleRate;
                buffer[offset + i] = v;
                if (i + 1 < count) buffer[offset + i + 1] = v;
            }
            System.Threading.Interlocked.Add(ref _read, count);
            try { OnRead?.Invoke(); } catch { }
            if (_delayMs > 0) System.Threading.Thread.Sleep(_delayMs);
            return count;
        }
    }

    [SkippableFact]
    public void DecodedOpusMp3_PlaysToCompletion()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        RequireOpenAl();

        using var source = AudioSourceFactory.Open(Fixtures.Audio.OpusMp3);
        using var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();

        output.Init((ISampleProvider)source);
        output.Play();

        Assert.True(stopped.Wait(15000), "decoded stream did not reach EOF");
    }
}
