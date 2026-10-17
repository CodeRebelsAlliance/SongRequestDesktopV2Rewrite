using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

internal static class AudioTestHelpers
{
    public static (double Rms, long Samples) DecodeAll(IAudioSource source, double seconds = 0)
    {
        var buffer = new float[4096];
        double sum = 0;
        long n = 0;
        long limit = seconds > 0
            ? (long)(seconds * source.WaveFormat.SampleRate * source.WaveFormat.Channels)
            : long.MaxValue;

        int read;
        while (n < limit && (read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++) sum += buffer[i] * buffer[i];
            n += read;
        }

        return (n > 0 ? Math.Sqrt(sum / n) : 0, n);
    }

    public static (double Rms, long Samples) DecodeWindowAt(string path, double startSeconds, double lengthSeconds)
    {
        using var source = AudioSourceFactory.Open(path);
        source.CurrentTime = TimeSpan.FromSeconds(startSeconds);
        return DecodeAll(source, lengthSeconds);
    }
}

/// <summary>Synthetic sample provider that yields a fixed number of samples then ends.</summary>
internal sealed class CountProvider : ISampleProvider
{
    private long _remaining;

    public CountProvider(int sampleRate, int channels, long samples, float value = 0.4f)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _remaining = samples;
        Value = value;
    }

    public WaveFormat WaveFormat { get; }
    public float Value { get; }
    public long TotalRead { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = (int)Math.Min(count, _remaining);
        for (int i = 0; i < n; i++) buffer[offset + i] = Value;
        _remaining -= n;
        TotalRead += n;
        return n;
    }
}
