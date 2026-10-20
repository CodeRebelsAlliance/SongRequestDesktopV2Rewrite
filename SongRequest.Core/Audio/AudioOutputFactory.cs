using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IAudioOutput"/> instances and enumerates output devices.
///
/// The platform host registers its backend at startup — OpenAL Soft on the
/// desktop, <c>AudioTrack</c>/<c>AVAudioEngine</c> on mobile. Engines use
/// <see cref="Create"/> as the default for their <c>OutputFactory</c> test seam,
/// so without a registered backend a silent <see cref="NullAudioOutput"/> is
/// handed out instead of throwing: engine construction must never fail before
/// the host has wired its platform up.
/// </summary>
public static class AudioOutputFactory
{
    /// <summary>Creates a platform audio output. Registered by the host.</summary>
    public static Func<IAudioOutput>? Provider { get; set; }

    /// <summary>
    /// Output factory for short soundboard clips. On mobile these deliberately do
    /// not take audio focus per clip: every overlay instance requesting focus made
    /// Android pause the previous clip, so "Overlay" sounded like a restart.
    /// </summary>
    public static Func<IAudioOutput>? SoundboardProvider { get; set; }

    /// <summary>Enumerates the selectable output devices. Registered by the host.</summary>
    public static Func<IReadOnlyList<AudioDeviceInfo>>? DeviceEnumerator { get; set; }

    /// <summary>Whether a real audio backend is available on this host.</summary>
    public static bool HasBackend => Provider != null;

    public static IAudioOutput Create() => Provider?.Invoke() ?? new NullAudioOutput();

    public static IAudioOutput CreateForSoundboard()
        => SoundboardProvider?.Invoke() ?? Create();

    /// <summary>Devices for the player/settings device menu (empty without a backend).</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
        => DeviceEnumerator?.Invoke() ?? Array.Empty<AudioDeviceInfo>();
}
