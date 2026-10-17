using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class FFmpegAudioSourceTests
{
    [SkippableFact]
    public void Metadata_IsReadFromProbe()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.Wav);

        Assert.Equal(44100, source.WaveFormat.SampleRate);
        Assert.Equal(2, source.WaveFormat.Channels);
        Assert.InRange(source.TotalTime.TotalSeconds, 2.7, 3.3);
    }

    [SkippableFact]
    public void Metadata_UsesNativeRate_For48kFile()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.M4a);

        Assert.Equal(48000, source.WaveFormat.SampleRate);
    }

    [SkippableFact]
    public void Decodes_Wav_Mp3_M4a_Flac()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");
        var a = Fixtures.Audio;
        DecodeNonSilent(a.Wav);
        DecodeNonSilent(a.Mp3);
        DecodeNonSilent(a.M4a);
        DecodeNonSilent(a.Flac);
    }

    [SkippableFact]
    public void Decodes_OpusInWebm_MislabeledAsMp3()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.OpusMp3);
        var (rms, samples) = AudioTestHelpers.DecodeAll(source);

        // 2 s silence + 2 s tone => non-silent overall
        Assert.True(rms > 0.2, $"rms={rms}");
        Assert.InRange(source.TotalTime.TotalSeconds, 3.7, 4.3);
        Assert.True(samples > 0);
    }

    [SkippableFact]
    public void Seek_ForwardAndBackward_LandInCorrectRegions()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var (silenceRms, _) = AudioTestHelpers.DecodeWindowAt(Fixtures.Audio.OpusMp3, 0.5, 0.3);
        var (toneRms, _) = AudioTestHelpers.DecodeWindowAt(Fixtures.Audio.OpusMp3, 3.0, 0.3);

        Assert.True(silenceRms < 0.05, $"silence rms={silenceRms}");
        Assert.True(toneRms > 0.3, $"tone rms={toneRms}");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.OpusMp3);
        AudioTestHelpers.DecodeAll(source, 1.0); // consume
        source.CurrentTime = TimeSpan.FromSeconds(3.0);
        var (forwardRms, _) = AudioTestHelpers.DecodeAll(source, 0.3);
        Assert.True(forwardRms > 0.3, $"forward rms={forwardRms}");

        source.CurrentTime = TimeSpan.FromSeconds(0.5);
        var (backRms, _) = AudioTestHelpers.DecodeAll(source, 0.3);
        Assert.True(backRms < 0.05, $"backward rms={backRms}");
    }

    [SkippableFact]
    public void Position_BytesMatchSamples()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        using var source = AudioSourceFactory.Open(Fixtures.Audio.Wav);
        Assert.Equal(0, source.Position);

        source.CurrentTime = TimeSpan.FromSeconds(1.0);
        long expected = (long)(1.0 * source.WaveFormat.SampleRate * source.WaveFormat.Channels * 4);
        Assert.InRange(source.Position, expected - source.WaveFormat.SampleRate * 8, expected + source.WaveFormat.SampleRate * 8);

        // forward skip (mimics the loudness scan) must not restart the decoder
        source.Position += (long)(0.5 * source.WaveFormat.SampleRate * source.WaveFormat.Channels * 4);
        var (rms, _) = AudioTestHelpers.DecodeAll(source, 0.2);
        Assert.True(rms > 0.3, $"post-skip rms={rms}");
    }

    private static void DecodeNonSilent(string path)
    {
        using var source = AudioSourceFactory.Open(path);
        var (rms, samples) = AudioTestHelpers.DecodeAll(source);
        var expected = (long)(source.TotalTime.TotalSeconds * source.WaveFormat.SampleRate * source.WaveFormat.Channels);

        Assert.True(rms > 0.3, $"{Path.GetFileName(path)} rms={rms}");
        Assert.InRange(samples, expected - source.WaveFormat.SampleRate, expected + source.WaveFormat.SampleRate);
        Assert.InRange(source.Length, samples * 4 - source.WaveFormat.SampleRate * 8, samples * 4 + source.WaveFormat.SampleRate * 8);
    }
}
