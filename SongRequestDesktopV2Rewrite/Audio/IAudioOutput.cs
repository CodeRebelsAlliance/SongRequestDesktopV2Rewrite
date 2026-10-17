using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Platform-agnostic audio output device.
///
/// Implemented by the cross-platform OpenAL Soft backend. The surface mirrors the
/// parts of NAudio's <c>WaveOutEvent</c> the app used.
/// </summary>
public interface IAudioOutput : IDisposable
{
    PlaybackState PlaybackState { get; }

    /// <summary>Master output volume (0..1). Used for announcement ducking.</summary>
    float Volume { get; set; }

    /// <summary>Device number, or -1 for the system default. Set before <see cref="Init(ISampleProvider)"/>.</summary>
    int DeviceNumber { get; set; }

    /// <summary>Raised when the device stops (end of stream or explicit stop).</summary>
    event EventHandler? PlaybackStopped;

    void Init(ISampleProvider source);

    void Init(IWaveProvider source);

    void Play();

    void Pause();

    void Stop();
}
