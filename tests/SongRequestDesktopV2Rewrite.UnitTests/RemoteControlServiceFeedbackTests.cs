using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class RemoteControlServiceFeedbackTests
{
    private static (RemoteControlService Svc, FakeMidiService Midi, MusicEngine Engine, Config Cfg) Create()
    {
        var cfg = new Config();
        cfg.RemoteControl.MidiEnabled = true;
        cfg.RemoteControl.PlayPauseMidi = new MidiMapping
        {
            MessageType = "NoteOn",
            Channel = 1,
            Note = 40,
            VelocityPressed = 127,
            VelocityUnpressed = 0,
        };
        cfg.RemoteControl.VolumeMidi = new MidiMapping { MessageType = "NoteOn", Channel = 1, Note = 41 };

        var midi = new FakeMidiService { IsEnabled = true };
        midi.ConnectOutput(0); // sets OutputDeviceNumber so feedback is emitted

        var svc = new RemoteControlService(midi) { ConfigAccessor = () => cfg };
        var engine = new MusicEngine { ConfigAccessor = () => cfg };
        svc.RegisterMusicEngine(engine);
        return (svc, midi, engine, cfg);
    }

    [Fact]
    public void PushFeedback_SendsPlayPauseAndVolumeValues()
    {
        var (svc, midi, engine, _) = Create();
        engine.Volume = 0.5f;
        try
        {
            svc.PushFeedbackSnapshot();

            // not playing -> unpressed velocity (0) -> NoteOff
            Assert.Contains(midi.Sent, m => m.Type == MidiMessageType.NoteOff && m.Data1 == 40);
            // volume 0.5 -> 64
            Assert.Contains(midi.Sent, m => m.Type == MidiMessageType.NoteOn && m.Data1 == 41 && m.Data2 == 64);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void PushFeedback_IsSuppressedWhenNothingChanged()
    {
        var (svc, midi, _, _) = Create();
        try
        {
            svc.PushFeedbackSnapshot();
            int afterFirst = midi.Sent.Count;

            svc.PushFeedbackSnapshot();

            Assert.Equal(afterFirst, midi.Sent.Count);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void PushFeedback_Playing_SendsPressedVelocity()
    {
        var (svc, midi, engine, _) = Create();
        var output = new FakeAudioOutput();
        engine.Output = output;
        output.Play();
        try
        {
            svc.PushFeedbackSnapshot();

            Assert.Contains(midi.Sent, m => m.Type == MidiMessageType.NoteOn && m.Data1 == 40 && m.Data2 == 127);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void PushFeedback_NoOutputDevice_DoesNothing()
    {
        var cfg = new Config();
        cfg.RemoteControl.MidiEnabled = true;
        cfg.RemoteControl.PlayPauseMidi = new MidiMapping { MessageType = "NoteOn", Channel = 1, Note = 40 };
        var midi = new FakeMidiService { IsEnabled = true }; // no ConnectOutput
        var svc = new RemoteControlService(midi) { ConfigAccessor = () => cfg };
        svc.RegisterMusicEngine(new MusicEngine { ConfigAccessor = () => cfg });
        try
        {
            svc.PushFeedbackSnapshot();
            Assert.Empty(midi.Sent);
        }
        finally { svc.Dispose(); }
    }
}
