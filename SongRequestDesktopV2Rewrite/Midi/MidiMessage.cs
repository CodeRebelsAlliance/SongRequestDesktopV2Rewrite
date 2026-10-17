namespace SongRequestDesktopV2Rewrite;

/// <summary>Backend-independent MIDI message type.</summary>
public enum MidiMessageType
{
    NoteOn,
    NoteOff,
    ControlChange,
    PitchWheel,
    Other
}

/// <summary>
/// Backend-independent MIDI message.
///
/// <see cref="Data1"/> is the note number / controller / pitch;
/// <see cref="Data2"/> is the velocity / controller value.
/// <see cref="RawType"/> preserves the original message-type string (for example
/// "NoteOn", "ControlChange", "PitchWheel") consumed by the web mapping UI.
/// </summary>
public readonly record struct MidiMessage(MidiMessageType Type, int Channel, int Data1, int Data2, string RawType);
