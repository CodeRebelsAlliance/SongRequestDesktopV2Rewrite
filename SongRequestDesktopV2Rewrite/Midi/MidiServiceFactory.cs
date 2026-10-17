using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IMidiService"/> instances using the cross-platform RtMidi
/// backend (WinMM / ALSA / CoreMIDI), or a no-op service when the platform has no
/// usable MIDI backend.
/// </summary>
public static class MidiServiceFactory
{
    /// <summary>Test seam: when set, this replaces the service handed out.</summary>
    internal static Func<IMidiService>? Override { get; set; }

    /// <summary>
    /// Whether native MIDI may be used on this machine.
    ///
    /// RtMidi talks to the ALSA sequencer on Linux; when <c>/dev/snd/seq</c> is
    /// missing (containers, minimal installs, some sandboxes) its ALSA backend
    /// throws a C++ exception through the P/Invoke boundary, which aborts the
    /// process rather than raising a catchable error. So the sequencer device is
    /// checked first and MIDI is simply reported as unavailable when it is absent.
    /// </summary>
    public static bool IsSupported =>
        IsPlatformSupported(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), AlsaSequencerAvailable());

    /// <summary>Pure decision, so every combination is unit-testable.</summary>
    internal static bool IsPlatformSupported(bool isWindows, bool isMacOS, bool isLinux, bool alsaSequencerAvailable)
        => isWindows || isMacOS || (isLinux && alsaSequencerAvailable);

    /// <summary>True when the ALSA sequencer device exists (Linux only).</summary>
    internal static bool AlsaSequencerAvailable()
    {
        if (!OperatingSystem.IsLinux()) return true;
        try { return File.Exists("/dev/snd/seq"); } catch { return false; }
    }

    public static IMidiService Create()
    {
        if (Override != null) return Override();
        if (!IsSupported) return new NullMidiService();

        try { return new RtMidiService(); }
        catch { return new NullMidiService(); }
    }
}
