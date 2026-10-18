using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>A selectable audio output device.</summary>
public sealed record AudioDeviceInfo(int Number, string Name);

/// <summary>
/// The output devices the platform backend offers. Delegates to
/// <see cref="AudioOutputFactory.DeviceEnumerator"/> so the core stays free of
/// any specific audio API; without a registered backend the list is empty.
/// </summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> Enumerate() => AudioOutputFactory.EnumerateDevices();
}
