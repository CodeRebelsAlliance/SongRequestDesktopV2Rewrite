namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Turns raw MIDI bytes into a <see cref="MidiMessage"/>.
///
/// The desktop's RtMidi hands us decoded messages; Android's MIDI API hands us
/// byte arrays. Keeping this parser in the core (and pure) means both produce the
/// same mapping — and it is unit-testable without a device.
/// </summary>
internal static class MidiBytesParser
{
    /// <summary>
    /// Parses one MIDI message. <paramref name="lastStatus"/> carries running
    /// status across calls (data bytes sent without a status byte).
    /// </summary>
    public static MidiMessage? Parse(byte[] data, int offset, int count, ref int lastStatus)
    {
        if (data == null || count <= 0 || offset < 0 || offset + count > data.Length) return null;

        int status = data[offset];
        int index = offset;

        if (status < 0x80)
        {
            // Running status: reuse the previous status byte.
            if (lastStatus < 0x80) return null;
            status = lastStatus;
        }
        else
        {
            lastStatus = status;
            index++;                       // skip the status byte
            if (status >= 0xF0) return null; // system messages carry no channel/note mapping
        }

        var type = status & 0xF0;
        int channel = status & 0x0F;

        if (index >= offset + count) return null;

        int data1 = data[index] & 0x7F;

        switch (type)
        {
            case 0x90:  // note on (velocity 0 = note off)
            {
                if (index + 1 >= offset + count) return null;
                int velocity = data[index + 1] & 0x7F;
                return velocity > 0
                    ? new MidiMessage(MidiMessageType.NoteOn, channel, data1, velocity, "NoteOn")
                    : new MidiMessage(MidiMessageType.NoteOff, channel, data1, 0, "NoteOff");
            }

            case 0x80:  // note off
            {
                int velocity = index + 1 < offset + count ? data[index + 1] & 0x7F : 0;
                return new MidiMessage(MidiMessageType.NoteOff, channel, data1, velocity, "NoteOff");
            }

            case 0xB0:  // control change
            {
                if (index + 1 >= offset + count) return null;
                int value = data[index + 1] & 0x7F;
                return new MidiMessage(MidiMessageType.ControlChange, channel, data1, value, "ControlChange");
            }

            case 0xE0:  // pitch wheel (14-bit, centre 8192)
            {
                int lsb = data1;
                int msb = index + 1 < offset + count ? data[index + 1] & 0x7F : 0;
                return new MidiMessage(MidiMessageType.PitchWheel, channel, lsb, msb, "PitchWheel");
            }

            case 0xA0:  // polyphonic aftertouch
            {
                int value = index + 1 < offset + count ? data[index + 1] & 0x7F : 0;
                return new MidiMessage(MidiMessageType.Other, channel, data1, value, "Aftertouch");
            }

            case 0xC0:  // program change (one data byte)
                return new MidiMessage(MidiMessageType.Other, channel, data1, 0, "ProgramChange");

            case 0xD0:  // channel aftertouch (one data byte)
                return new MidiMessage(MidiMessageType.Other, channel, data1, 0, "ChannelAftertouch");

            default:
                return new MidiMessage(MidiMessageType.Other, channel, data1, 0, $"Status{status:X2}");
        }
    }
}
