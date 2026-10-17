namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IMidiService"/> instances using the cross-platform RtMidi
/// backend (WinMM / ALSA / CoreMIDI).
/// </summary>
public static class MidiServiceFactory
{
    public static IMidiService Create() => new RtMidiService();
}
