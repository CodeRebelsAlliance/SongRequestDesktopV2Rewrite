namespace SongRequestDesktopV2Rewrite
{
    public class RemoteControlConfiguration
    {
        public KeyboardShortcutConfig PlayPauseKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig SkipNextKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig PreviousKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig StopKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig VolumeUpKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig VolumeDownKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig CrossfadeUpKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig CrossfadeDownKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig AnnouncementKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig AnnouncementPlaySoundToggleKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig AnnouncementPushToTalkToggleKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig AnnouncementDimDbUpKeybind { get; set; } = new KeyboardShortcutConfig();
        public KeyboardShortcutConfig AnnouncementDimDbDownKeybind { get; set; } = new KeyboardShortcutConfig();

        public bool MidiEnabled { get; set; } = true;
        public int MidiInputDevice { get; set; } = -1;
        public int MidiOutputDevice { get; set; } = -1;

        public MidiMapping? PlayPauseMidi { get; set; }
        public MidiMapping? SkipNextMidi { get; set; }
        public MidiMapping? PreviousMidi { get; set; }
        public MidiMapping? StopMidi { get; set; }
        public MidiMapping? VolumeMidi { get; set; }
        public MidiMapping? CrossfadeMidi { get; set; }
        public MidiMapping? AnnouncementMidi { get; set; }
        public MidiMapping? AnnouncementPlaySoundToggleMidi { get; set; }
        public MidiMapping? AnnouncementPushToTalkToggleMidi { get; set; }
        public MidiMapping? AnnouncementDimDbMidi { get; set; }

        public static RemoteControlConfiguration Ensure(RemoteControlConfiguration? value)
        {
            value ??= new RemoteControlConfiguration();
            value.PlayPauseKeybind ??= new KeyboardShortcutConfig();
            value.SkipNextKeybind ??= new KeyboardShortcutConfig();
            value.PreviousKeybind ??= new KeyboardShortcutConfig();
            value.StopKeybind ??= new KeyboardShortcutConfig();
            value.VolumeUpKeybind ??= new KeyboardShortcutConfig();
            value.VolumeDownKeybind ??= new KeyboardShortcutConfig();
            value.CrossfadeUpKeybind ??= new KeyboardShortcutConfig();
            value.CrossfadeDownKeybind ??= new KeyboardShortcutConfig();
            value.AnnouncementKeybind ??= new KeyboardShortcutConfig();
            value.AnnouncementPlaySoundToggleKeybind ??= new KeyboardShortcutConfig();
            value.AnnouncementPushToTalkToggleKeybind ??= new KeyboardShortcutConfig();
            value.AnnouncementDimDbUpKeybind ??= new KeyboardShortcutConfig();
            value.AnnouncementDimDbDownKeybind ??= new KeyboardShortcutConfig();
            return value;
        }
    }
}
