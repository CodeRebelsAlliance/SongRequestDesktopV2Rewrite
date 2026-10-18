using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The MIDI byte parser the mobile backend uses. Android's MIDI API hands the app
/// raw bytes, the desktop's RtMidi hands it decoded messages, and both must reach
/// the same <see cref="MidiMessage"/> — the soundboard pads and the remote-control
/// mappings are keyed on note/controller numbers, so a mapping mistake here is a
/// pad that never fires.
/// </summary>
public class MidiBytesParserTests
{
    private static MidiMessage? Parse(params byte[] data)
    {
        int lastStatus = 0;
        return MidiBytesParser.Parse(data, 0, data.Length, ref lastStatus);
    }

    [Fact]
    public void NoteOn_IsParsedWithChannelNoteAndVelocity()
    {
        var message = Parse(0x90, 60, 100);

        Assert.NotNull(message);
        Assert.Equal(MidiMessageType.NoteOn, message!.Value.Type);
        Assert.Equal(0, message.Value.Channel);
        Assert.Equal(60, message.Value.Data1);
        Assert.Equal(100, message.Value.Data2);
        Assert.Equal("NoteOn", message.Value.RawType);
    }

    [Fact]
    public void NoteOn_WithZeroVelocity_IsANoteOff()
    {
        // Controllers commonly release a pad this way.
        var message = Parse(0x95, 60, 0);

        Assert.NotNull(message);
        Assert.Equal(MidiMessageType.NoteOff, message!.Value.Type);
        Assert.Equal(5, message.Value.Channel);
        Assert.Equal("NoteOff", message.Value.RawType);
    }

    [Fact]
    public void NoteOff_KeepsTheReleaseVelocity()
    {
        var message = Parse(0x80, 42, 64);

        Assert.NotNull(message);
        Assert.Equal(MidiMessageType.NoteOff, message!.Value.Type);
        Assert.Equal(42, message.Value.Data1);
        Assert.Equal(64, message.Value.Data2);
    }

    [Fact]
    public void ControlChange_IsParsedWithControllerAndValue()
    {
        var message = Parse(0xB2, 7, 127);

        Assert.NotNull(message);
        Assert.Equal(MidiMessageType.ControlChange, message!.Value.Type);
        Assert.Equal(2, message.Value.Channel);
        Assert.Equal(7, message.Value.Data1);
        Assert.Equal(127, message.Value.Data2);
    }

    [Fact]
    public void PitchWheel_KeepsBothBytes()
    {
        var message = Parse(0xE0, 0x00, 0x40);   // centre

        Assert.NotNull(message);
        Assert.Equal(MidiMessageType.PitchWheel, message!.Value.Type);
        Assert.Equal(0x00, message.Value.Data1);
        Assert.Equal(0x40, message.Value.Data2);
    }

    [Fact]
    public void DataBytesWithoutAStatus_ReuseTheRunningStatus()
    {
        // A controller that sends one status byte and then streams notes.
        // One status byte, then three notes: 0x90 60 100 | 62 100 | 64 100
        var data = new byte[] { 0x90, 60, 100, 62, 100, 64, 100, 65, 100 };
        int lastStatus = 0;

        var first = MidiBytesParser.Parse(data, 0, 3, ref lastStatus);
        var second = MidiBytesParser.Parse(data, 3, 3, ref lastStatus);
        var third = MidiBytesParser.Parse(data, 5, 3, ref lastStatus);

        Assert.Equal(60, first!.Value.Data1);
        Assert.Equal(62, second!.Value.Data1);
        Assert.Equal(64, third!.Value.Data1);

        Assert.Equal(MidiMessageType.NoteOn, first.Value.Type);
        Assert.Equal(MidiMessageType.NoteOn, second.Value.Type);
        Assert.Equal(MidiMessageType.NoteOn, third.Value.Type);
    }

    [Fact]
    public void DataByteWithNoRunningStatus_IsIgnored()
    {
        int lastStatus = 0;
        Assert.Null(MidiBytesParser.Parse(new byte[] { 60, 100 }, 0, 2, ref lastStatus));
    }

    [Fact]
    public void SystemMessages_AreIgnored()
    {
        Assert.Null(Parse(0xF8));              // clock
        Assert.Null(Parse(0xFE));              // active sensing
    }

    [Fact]
    public void TruncatedMessages_AreIgnoredInsteadOfThrowing()
    {
        Assert.Null(Parse(0x90));              // note on with no note
        Assert.Null(Parse(0x90, 60));          // note on with no velocity
        Assert.Null(Parse(0xB0));              // control change with no data
    }

    [Fact]
    public void OutOfRangeBuffers_AreIgnored()
    {
        int lastStatus = 0;
        Assert.Null(MidiBytesParser.Parse(Array.Empty<byte>(), 0, 1, ref lastStatus));
        Assert.Null(MidiBytesParser.Parse(new byte[] { 0x90, 60, 100 }, 5, 3, ref lastStatus));
        Assert.Null(MidiBytesParser.Parse(new byte[] { 0x90, 60, 100 }, 0, 0, ref lastStatus));
    }

    [Fact]
    public void HighBitsInDataBytes_AreMasked()
    {
        // Some devices set the high bit on data bytes; only 7 bits are meaningful.
        var message = Parse(0x90, 0xE0, 0xFF);

        Assert.NotNull(message);
        Assert.Equal(0x60, message!.Value.Data1);
        Assert.Equal(0x7F, message.Value.Data2);
    }
}
