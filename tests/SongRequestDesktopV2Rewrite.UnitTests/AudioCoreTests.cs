using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class AudioClipTests
{
    [Fact]
    public void Duration_FrameCount_AndByteSize_AreDerivedFromSamples()
    {
        var samples = new float[44100 * 2 * 2]; // 2 s stereo
        var clip = new AudioClip(samples, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        Assert.Equal(2, clip.Channels);
        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(44100 * 2, clip.FrameCount);
        Assert.Equal(2.0, clip.Duration.TotalSeconds, 3);
        Assert.Equal(samples.Length * 4L, clip.ByteSize);
    }

    [Fact]
    public void MonoClip_ReportsOneChannel()
    {
        var clip = new AudioClip(new float[100], WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));

        Assert.Equal(1, clip.Channels);
        Assert.Equal(48000, clip.SampleRate);
        Assert.Equal(100, clip.FrameCount);
    }
}

public class InMemoryAudioSourceTests
{
    private static AudioClip MakeClip(int frames = 10, int channels = 2, float value = 0.5f)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++) samples[i] = value;
        return new AudioClip(samples, WaveFormat.CreateIeeeFloatWaveFormat(44100, channels));
    }

    [Fact]
    public void Read_ReturnsAllSamplesThenZero()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 10));
        var buffer = new float[20];

        Assert.Equal(20, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Position_IsByteBased_AndSupportsSeek()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 10));
        Assert.Equal(0, source.Position);

        source.Position = 5 * 4; // 5 samples in
        Assert.Equal(5, source.Position / 4);

        var buffer = new float[100];
        Assert.Equal(20 - 5, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Position_IsClampedToClipBounds()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 10));

        source.Position = long.MaxValue;
        Assert.Equal(20 * 4L, source.Position);

        source.Position = -100;
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void Volume_ScalesOutput()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 4, value: 0.5f)) { Volume = 0.5f };
        var buffer = new float[8];

        source.Read(buffer, 0, buffer.Length);

        Assert.All(buffer, v => Assert.Equal(0.25f, v, 5));
    }

    [Fact]
    public void CurrentTime_ReflectsAndSetsPosition()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 44100)); // 1 s stereo

        source.CurrentTime = TimeSpan.FromSeconds(0.5);

        Assert.Equal(0.5, source.CurrentTime.TotalSeconds, 3);
    }

    [Fact]
    public void Length_AndTotalTime_AreConsistent()
    {
        using var source = new InMemoryAudioSource(MakeClip(frames: 44100));

        Assert.Equal(44100L * 2 * 4, source.Length);
        Assert.Equal(1.0, source.TotalTime.TotalSeconds, 3);
    }

    [Fact]
    public void CursorsOverTheSameClip_AreIndependent()
    {
        var clip = MakeClip(frames: 10);
        using var first = new InMemoryAudioSource(clip);
        using var second = new InMemoryAudioSource(clip);
        var buffer = new float[10];

        first.Read(buffer, 0, buffer.Length);

        Assert.Equal(0, second.Position);
        Assert.Equal(20, second.Read(new float[20], 0, 20));
    }
}
