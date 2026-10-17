using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class OpenAlAudioOutputTests
{
    [SkippableFact]
    public void Constructs_WithNullDriver()
    {
        using var output = new OpenAlAudioOutput();
        Assert.Equal(PlaybackState.Stopped, output.PlaybackState);
    }

    [SkippableFact]
    public void PlaysSyntheticStream_ToCompletion_AndRaisesStopped()
    {
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

    [SkippableFact]
    public void DecodedOpusMp3_PlaysToCompletion()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.OpusMp3);
        using var output = new OpenAlAudioOutput();
        var stopped = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => stopped.Set();

        output.Init((ISampleProvider)source);
        output.Play();

        Assert.True(stopped.Wait(15000), "decoded stream did not reach EOF");
    }
}
