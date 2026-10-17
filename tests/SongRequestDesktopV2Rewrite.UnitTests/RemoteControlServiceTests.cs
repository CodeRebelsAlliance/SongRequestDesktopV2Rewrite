using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class RemoteControlServiceTests
{
    private static readonly RemoteControlService Service = RemoteControlService.Instance;

    [Fact]
    public void MatchMidiMapping_TrueForMatchingNoteOn()
    {
        var mapping = new MidiMapping { Channel = 3, Note = 60, MessageType = "NoteOn" };

        Assert.True(Service.MatchMidiMapping(mapping, "NoteOn", 3, 60, 100, RemoteControlService.RemoteAction.PlayPause));
    }

    [Theory]
    [InlineData("ControlChange", 3, 60)] // wrong message type
    [InlineData("NoteOn", 2, 60)]        // wrong channel
    [InlineData("NoteOn", 3, 61)]        // wrong note
    public void MatchMidiMapping_FalseWhenAFieldDiffers(string type, int channel, int note)
    {
        var mapping = new MidiMapping { Channel = 3, Note = 60, MessageType = "NoteOn" };

        Assert.False(Service.MatchMidiMapping(mapping, type, channel, note, 100, RemoteControlService.RemoteAction.PlayPause));
    }

    [Fact]
    public void MatchMidiMapping_IsCaseInsensitiveOnType()
    {
        var mapping = new MidiMapping { Channel = 1, Note = 40, MessageType = "noteon" };

        Assert.True(Service.MatchMidiMapping(mapping, "NoteOn", 1, 40, 100, RemoteControlService.RemoteAction.PlayPause));
    }

    [Fact]
    public void MatchMidiMapping_FalseForNullOrUnconfigured()
    {
        Assert.False(Service.MatchMidiMapping(null, "NoteOn", 1, 60, 100, RemoteControlService.RemoteAction.PlayPause));

        var unconfigured = new MidiMapping { Note = -1, Channel = 1, MessageType = "NoteOn" };
        Assert.False(Service.MatchMidiMapping(unconfigured, "NoteOn", 1, -1, 100, RemoteControlService.RemoteAction.PlayPause));
    }
}
