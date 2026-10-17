using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class RemoteControlServiceDispatchTests
{
    private static (RemoteControlService Svc, FakeMidiService Midi, MusicEngine Engine, Config Cfg) Create()
    {
        var cfg = new Config { AnnouncementPlaySound = true, AnnouncementPushToTalk = true };
        cfg.RemoteControl.MidiEnabled = true;

        var midi = new FakeMidiService();
        var svc = new RemoteControlService(midi) { ConfigAccessor = () => cfg };
        var engine = new MusicEngine { ConfigAccessor = () => cfg, ConfigUpdater = updater => updater(cfg) };
        svc.RegisterMusicEngine(engine);
        return (svc, midi, engine, cfg);
    }

    [Fact]
    public void ControlChange_VolumeAbsolute_SetsEngineVolume()
    {
        var (svc, midi, engine, cfg) = Create();
        cfg.RemoteControl.VolumeMidi = new MidiMapping { MessageType = "ControlChange", Channel = 1, Note = 7 };
        try
        {
            midi.Raise(new MidiMessage(MidiMessageType.ControlChange, 1, 7, 64, "ControlChange"));
            Assert.Equal(64 / 127.0, engine.Volume, 3);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void ControlChange_CrossfadeAbsolute_SetsCrossfade()
    {
        var (svc, midi, engine, cfg) = Create();
        cfg.RemoteControl.CrossfadeMidi = new MidiMapping { MessageType = "ControlChange", Channel = 2, Note = 8 };
        try
        {
            midi.Raise(new MidiMessage(MidiMessageType.ControlChange, 2, 8, 127, "ControlChange"));
            Assert.Equal(10, engine.CrossfadeSeconds, 3); // max
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void ControlChange_AnnouncementDimAbsolute_WritesConfig()
    {
        var (svc, midi, _, cfg) = Create();
        cfg.RemoteControl.AnnouncementDimDbMidi = new MidiMapping { MessageType = "ControlChange", Channel = 1, Note = 9 };
        try
        {
            midi.Raise(new MidiMessage(MidiMessageType.ControlChange, 1, 9, 127, "ControlChange"));
            Assert.Equal(50.0, cfg.AnnouncementDimDb, 3);

            midi.Raise(new MidiMessage(MidiMessageType.ControlChange, 1, 9, 0, "ControlChange"));
            Assert.Equal(0.0, cfg.AnnouncementDimDb, 3);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void NoteOn_AnnouncementPlaySoundToggle_FlipsConfig()
    {
        var (svc, midi, _, cfg) = Create();
        cfg.RemoteControl.AnnouncementPlaySoundToggleMidi = new MidiMapping { MessageType = "NoteOn", Channel = 1, Note = 20 };
        try
        {
            Assert.True(cfg.AnnouncementPlaySound);
            midi.Raise(new MidiMessage(MidiMessageType.NoteOn, 1, 20, 100, "NoteOn"));
            Assert.False(cfg.AnnouncementPlaySound);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void NoteOn_AnnouncementPushToTalkToggle_FlipsConfig()
    {
        var (svc, midi, _, cfg) = Create();
        cfg.RemoteControl.AnnouncementPushToTalkToggleMidi = new MidiMapping { MessageType = "NoteOn", Channel = 1, Note = 21 };
        try
        {
            Assert.True(cfg.AnnouncementPushToTalk);
            midi.Raise(new MidiMessage(MidiMessageType.NoteOn, 1, 21, 100, "NoteOn"));
            Assert.False(cfg.AnnouncementPushToTalk);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void NoteOn_Announcement_TriggersAndReleases()
    {
        var (svc, midi, engine, cfg) = Create();
        cfg.RemoteControl.AnnouncementMidi = new MidiMapping { MessageType = "NoteOn", Channel = 1, Note = 30 };
        bool triggered = false, released = false;
        engine.RemoteAnnouncementHandler = () => { triggered = true; return Task.CompletedTask; };
        engine.RemoteAnnouncementReleaseHandler = () => { released = true; return Task.CompletedTask; };
        try
        {
            midi.Raise(new MidiMessage(MidiMessageType.NoteOn, 1, 30, 100, "NoteOn"));
            Assert.True(triggered);

            midi.Raise(new MidiMessage(MidiMessageType.NoteOn, 1, 30, 0, "NoteOn"));
            Assert.True(released);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void UnmatchedMessage_DoesNotTrigger()
    {
        var (svc, midi, engine, cfg) = Create();
        cfg.RemoteControl.VolumeMidi = new MidiMapping { MessageType = "ControlChange", Channel = 1, Note = 7 };
        float before = engine.Volume;
        try
        {
            midi.Raise(new MidiMessage(MidiMessageType.ControlChange, 9, 99, 64, "ControlChange"));
            Assert.Equal(before, engine.Volume);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void GlobalHotkey_TriggersTheMappedAction()
    {
        var cfg = new Config { GlobalKeyboardShortcuts = true };
        cfg.RemoteControl.VolumeUpKeybind = new KeyboardShortcutConfig { Enabled = true, Gesture = "Ctrl+Alt+Up" };

        var backend = new FakeGlobalHotkeyBackend();
        var svc = new RemoteControlService(new FakeMidiService(), backend) { ConfigAccessor = () => cfg };
        var engine = new MusicEngine { ConfigAccessor = () => cfg, Volume = 0.5f };
        svc.RegisterMusicEngine(engine);
        try
        {
            Assert.True(svc.GlobalShortcutsSupported);
            Assert.Equal(1, svc.ApplyGlobalShortcuts(cfg.RemoteControl));

            backend.Raise(backend.Registered[0].Id);

            Assert.Equal(0.53f, engine.Volume, 3); // VolumeUp = +0.03
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void GlobalHotkey_NotRegistered_WhenDisabledOrUnsupported()
    {
        var cfg = new Config { GlobalKeyboardShortcuts = false };
        cfg.RemoteControl.VolumeUpKeybind = new KeyboardShortcutConfig { Enabled = true, Gesture = "Ctrl+Alt+Up" };

        var backend = new FakeGlobalHotkeyBackend();
        var svc = new RemoteControlService(new FakeMidiService(), backend) { ConfigAccessor = () => cfg };
        try
        {
            Assert.Equal(0, svc.ApplyGlobalShortcuts(cfg.RemoteControl));

            cfg.GlobalKeyboardShortcuts = true;
            backend.IsSupported = false;
            Assert.Equal(0, svc.ApplyGlobalShortcuts(cfg.RemoteControl));

            Assert.Empty(backend.Registered);
        }
        finally { svc.Dispose(); }
    }
}
