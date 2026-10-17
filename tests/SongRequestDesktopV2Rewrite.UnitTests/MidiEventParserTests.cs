using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MidiEventParserTests
{
    [Fact]
    public void Parse_NoteOn_MapsFields()
    {
        var parsed = MidiEventParser.Parse(new MidiMessage(MidiMessageType.NoteOn, 3, 60, 100, "NoteOn"))!;

        Assert.Equal("NoteOn", TestSupport.Prop<string>(parsed, "messageType"));
        Assert.Equal(3, TestSupport.Prop<int>(parsed, "channel"));
        Assert.Equal(60, TestSupport.Prop<int>(parsed, "value1"));
        Assert.Equal(100, TestSupport.Prop<int>(parsed, "value2"));
    }

    [Fact]
    public void Parse_ControlChange_MapsFields()
    {
        var parsed = MidiEventParser.Parse(new MidiMessage(MidiMessageType.ControlChange, 2, 7, 42, "ControlChange"))!;

        Assert.Equal("ControlChange", TestSupport.Prop<string>(parsed, "messageType"));
        Assert.Equal(2, TestSupport.Prop<int>(parsed, "channel"));
        Assert.Equal(7, TestSupport.Prop<int>(parsed, "value1"));
        Assert.Equal(42, TestSupport.Prop<int>(parsed, "value2"));
    }

    [Fact]
    public void Parse_PitchWheel_Value2IsZero_AndTypeIsPitchWheel()
    {
        var parsed = MidiEventParser.Parse(new MidiMessage(MidiMessageType.PitchWheel, 1, 8192, 0, "PitchWheel"))!;

        Assert.Equal("PitchWheel", TestSupport.Prop<string>(parsed, "messageType"));
        Assert.Equal(8192, TestSupport.Prop<int>(parsed, "value1"));
        Assert.Equal(0, TestSupport.Prop<int>(parsed, "value2"));
    }
}
