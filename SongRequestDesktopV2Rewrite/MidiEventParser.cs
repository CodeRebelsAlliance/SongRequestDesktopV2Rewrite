using System;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Parses a backend-independent <see cref="MidiMessage"/> into a
    /// browser-friendly shape for the web settings UI (MIDI assignment capture).
    /// Mirrors the logic that used to live in <c>MidiAssignDialog</c>.
    /// </summary>
    internal static class MidiEventParser
    {
        public static object? Parse(MidiMessage message)
        {
            try
            {
                return new
                {
                    messageType = message.RawType,
                    channel = message.Channel,
                    value1 = message.Data1,
                    value2 = message.Data2
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
