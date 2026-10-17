using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Platform-agnostic decoded audio source.
///
/// Implemented by the cross-platform FFmpeg-backed <see cref="FFmpegAudioSource"/>.
/// The surface mirrors the parts of NAudio's <c>AudioFileReader</c> the app used.
/// </summary>
public interface IAudioSource : IDisposable
{
    WaveFormat WaveFormat { get; }

    TimeSpan TotalTime { get; }

    /// <summary>Playback / seek position (used by seeking and queue timing).</summary>
    TimeSpan CurrentTime { get; set; }

    /// <summary>Raw byte length of the source.</summary>
    long Length { get; }

    /// <summary>Raw byte position (used by looping and loudness sampling).</summary>
    long Position { get; set; }

    /// <summary>Per-source gain. May exceed 1.0 (the soundboard boosts up to 3×).</summary>
    float Volume { get; set; }

    int Read(float[] buffer, int offset, int count);

    ISampleProvider ToSampleProvider();
}
