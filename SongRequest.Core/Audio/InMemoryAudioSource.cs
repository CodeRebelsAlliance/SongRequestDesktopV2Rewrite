using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Plays a fully-decoded <see cref="AudioClip"/> from memory with zero decode
/// latency. Safe to use concurrently — each instance has its own cursor over
/// the shared sample buffer.
/// </summary>
internal sealed class InMemoryAudioSource : IAudioSource, ISampleProvider
{
    private readonly AudioClip _clip;
    private long _position; // interleaved sample index
    private float _volume = 1f;

    public InMemoryAudioSource(AudioClip clip) => _clip = clip;

    public WaveFormat WaveFormat => _clip.WaveFormat;

    public TimeSpan TotalTime => _clip.Duration;

    public TimeSpan CurrentTime
    {
        get => TimeSpan.FromSeconds((double)_position / (_clip.SampleRate * _clip.Channels));
        set => _position = Math.Clamp((long)(value.TotalSeconds * _clip.SampleRate * _clip.Channels), 0, _clip.Samples.LongLength);
    }

    public long Length => _clip.Samples.LongLength * 4L;

    public long Position
    {
        get => _position * 4L;
        set => _position = Math.Clamp(value / 4L, 0, _clip.Samples.LongLength);
    }

    public float Volume
    {
        get => _volume;
        set => _volume = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long remaining = _clip.Samples.LongLength - _position;
        if (remaining <= 0 || count <= 0) return 0;

        int n = (int)Math.Min(count, remaining);
        Array.Copy(_clip.Samples, _position, buffer, offset, n);
        _position += n;

        if (_volume != 1f)
        {
            for (int i = 0; i < n; i++) buffer[offset + i] *= _volume;
        }

        return n;
    }

    public ISampleProvider ToSampleProvider() => this;

    public void Dispose() { }
}
