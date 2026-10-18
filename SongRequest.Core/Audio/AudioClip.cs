using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Immutable, fully-decoded PCM clip shared by any number of
/// <see cref="InMemoryAudioSource"/> playback cursors.
/// </summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples, WaveFormat waveFormat)
    {
        Samples = samples;
        WaveFormat = waveFormat;
        Channels = waveFormat.Channels <= 0 ? 2 : waveFormat.Channels;
        SampleRate = waveFormat.SampleRate <= 0 ? 44100 : waveFormat.SampleRate;
    }

    /// <summary>Interleaved 32-bit float samples.</summary>
    public float[] Samples { get; }

    public WaveFormat WaveFormat { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public long FrameCount => Channels > 0 ? Samples.LongLength / Channels : 0;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.LongLength / (SampleRate * Channels));

    public long ByteSize => Samples.LongLength * 4L;
}
