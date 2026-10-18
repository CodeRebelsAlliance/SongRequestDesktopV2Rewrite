using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Registers the desktop implementations of the core's platform seams:
/// OpenAL Soft (audio output), RtMidi (MIDI), MPRIS/SMTC (system media controls),
/// Win32/X11 global hotkeys and the Win32/AppIndicator tray.
///
/// Called once at startup by <see cref="Program"/> and by the test hosts, so the
/// desktop behaves exactly as it did before the platform seams were introduced.
/// A mobile host registers its own backends instead and never calls this.
/// </summary>
internal static class DesktopPlatform
{
    private static readonly object Sync = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Sync)
        {
            if (_registered) return;
            _registered = true;
        }

        // ── Audio output: OpenAL Soft, plus its device list ───────────────────
        AudioOutputFactory.Provider = () => new OpenAlAudioOutput();
        AudioOutputFactory.DeviceEnumerator = EnumerateOpenAlDevices;

        // ── MIDI: RtMidi (WinMM / ALSA / CoreMIDI), guarded per platform ──────
        // The guard lives here (not in the factory) because it exists to avoid a
        // native abort: RtMidi's ALSA backend throws through the P/Invoke
        // boundary when /dev/snd/seq is missing.
        MidiServiceFactory.BackendSupported = () =>
            MidiServiceFactory.IsPlatformSupported(
                OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(),
                MidiServiceFactory.AlsaSequencerAvailable());
        MidiServiceFactory.BackendFactory = () =>
            MidiServiceFactory.IsSupported ? new RtMidiService() : new NullMidiService();

        // ── System media controls: MPRIS (Linux) / SMTC (Windows) ─────────────
        MediaSessionFactory.Register(new DesktopMediaSessionBackend());

        // ── Global hotkeys: RegisterHotKey (Windows) / XGrabKey (X11) ─────────
        GlobalHotkeyBackendFactory.BackendFactory = DesktopHotkeyBackends.Create;

        // ── Tray: Shell_NotifyIcon (Windows) / AppIndicator (Linux) ───────────
        TrayIconFactory.IsSupportedProvider = () => DesktopTrayIcons.IsSupported;
        TrayIconFactory.BackendFactory = DesktopTrayIcons.Create;
    }

    private static IReadOnlyList<AudioDeviceInfo> EnumerateOpenAlDevices()
    {
        var names = OpenAlDevices.Names();
        var list = new List<AudioDeviceInfo>(names.Count);
        for (int i = 0; i < names.Count; i++)
            list.Add(new AudioDeviceInfo(i, names[i]));
        return list;
    }
}
