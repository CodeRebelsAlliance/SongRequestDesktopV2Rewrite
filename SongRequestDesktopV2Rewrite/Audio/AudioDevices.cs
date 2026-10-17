using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>A selectable audio output device.</summary>
public sealed record AudioDeviceInfo(int Number, string Name);

/// <summary>
/// Enumerates available audio output devices via OpenAL Soft.
/// </summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        var names = OpenAlDevices.Names();
        var list = new List<AudioDeviceInfo>(names.Count);
        for (int i = 0; i < names.Count; i++)
            list.Add(new AudioDeviceInfo(i, names[i]));
        return list;
    }
}
