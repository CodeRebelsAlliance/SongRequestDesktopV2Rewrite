using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IMidiService"/> instances.
///
/// The platform host registers its backend through <see cref="BackendFactory"/>
/// (RtMidi on the desktop — WinMM / ALSA / CoreMIDI; <c>MidiManager</c> /
/// CoreMIDI on mobile). Without a registered backend a no-op service is handed
/// out, so the soundboard and remote control keep working with MIDI absent.
/// </summary>
public static class MidiServiceFactory
{
    /// <summary>Test seam: when set, this replaces the service handed out.</summary>
    internal static Func<IMidiService>? Override { get; set; }

    /// <summary>
    /// Platform backend, registered by the host. The backend decides whether MIDI
    /// is usable on this machine (the desktop backend checks
    /// <see cref="IsSupported"/> before touching RtMidi) and may return a no-op
    /// service when it is not.
    /// </summary>
    public static Func<IMidiService>? BackendFactory { get; set; }

    /// <summary>
    /// Whether the registered backend can actually be used, as reported by the
    /// host (desktop: the ALSA sequencer guard; mobile: the platform MIDI API is
    /// always present on supported versions). Drives the settings UI and the
    /// integration tests' skip guard.
    /// </summary>
    public static Func<bool>? BackendSupported { get; set; }

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
        BackendSupported?.Invoke() ??
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

        var backend = BackendFactory;
        if (backend == null) return new NullMidiService();

        try { return backend(); }
        catch { return new NullMidiService(); }
    }
}
