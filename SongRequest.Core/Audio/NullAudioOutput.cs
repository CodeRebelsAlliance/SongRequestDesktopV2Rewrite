using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Silent <see cref="IAudioOutput"/> used when the host has not registered an
/// audio backend (unit tests, or a host before its platform initialisation).
///
/// It reports the playback state it is asked for and consumes nothing, so
/// engine logic (queue advance, crossfade bookkeeping, progress) can still be
/// exercised without touching a device.
/// </summary>
public sealed class NullAudioOutput : IAudioOutput
{
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    public float Volume { get; set; } = 1f;

    public int DeviceNumber { get; set; } = -1;

    public event EventHandler? PlaybackStopped;

    public void Init(ISampleProvider source) { }

    public void Init(IWaveProvider source) { }

    public void Play() => PlaybackState = PlaybackState.Playing;

    public void Pause() => PlaybackState = PlaybackState.Paused;

    public void Stop()
    {
        var wasActive = PlaybackState != PlaybackState.Stopped;
        PlaybackState = PlaybackState.Stopped;
        if (wasActive) PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() { }
}
