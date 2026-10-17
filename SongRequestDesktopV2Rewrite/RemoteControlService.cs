using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite
{
    public sealed class RemoteControlService : IDisposable
    {
        private static readonly Lazy<RemoteControlService> _instance = new(() => new RemoteControlService());
        public static RemoteControlService Instance => _instance.Value;

        private readonly IMidiService _midiService;
        private readonly GlobalHotkeyService _hotkeys;
        private readonly System.Timers.Timer _feedbackTimer;
        private MusicEngine? _musicEngine;
        private bool _initialized;
        private bool _sendingFeedback;
        private readonly object _midiDeviceSwitchLock = new object();
        private int? _lastPlayPauseFeedback;
        private int? _lastVolumeFeedback;
        private int? _lastCrossfadeFeedback;
        private int? _lastAnnouncementFeedback;
        private int? _lastAnnouncementDimFeedback;

        public IMidiService MidiService => _midiService;
        public event EventHandler<string>? MidiActivity;

        /// <summary>Whether this platform supports system-wide hotkeys.</summary>
        public bool GlobalShortcutsSupported => _hotkeys.IsSupported;

        /// <summary>Number of global hotkeys currently registered.</summary>
        public int RegisteredGlobalShortcuts { get; private set; }

        /// <summary>Provides the current configuration (overridable in tests).</summary>
        public Func<Config?> ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;

        private RemoteControlService()
            : this(MidiServiceFactory.Create())
        {
        }

        internal RemoteControlService(IMidiService midiService)
            : this(midiService, null)
        {
        }

        /// <summary>Creates an isolated instance (used by tests).</summary>
        internal RemoteControlService(IMidiService midiService, IGlobalHotkeyBackend? hotkeyBackend)
        {
            _midiService = midiService;
            _hotkeys = new GlobalHotkeyService(hotkeyBackend ?? GlobalHotkeyBackendFactory.Create());
            _hotkeys.ActionTriggered += OnGlobalHotkeyTriggered;
            _feedbackTimer = new System.Timers.Timer(150) { AutoReset = true };
            _feedbackTimer.Elapsed += (_, _) => PushFeedbackSnapshot();
            _midiService.MidiMessageReceived += MidiService_MidiMessageReceived;
            _midiService.ErrorOccurred += (_, message) => MidiActivity?.Invoke(this, $"MIDI error: {message}");
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            ApplyConfig();
            _feedbackTimer.Start();
        }

        public void RegisterMusicEngine(MusicEngine engine)
        {
            _musicEngine = engine;
        }

        public void ApplyConfig()
        {
            var cfg = RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl);
            ApplyMidiDevices(cfg.MidiInputDevice, cfg.MidiOutputDevice, cfg.MidiEnabled);
            ApplyGlobalShortcuts(cfg);
            _lastPlayPauseFeedback = null;
            _lastVolumeFeedback = null;
            _lastCrossfadeFeedback = null;
            _lastAnnouncementFeedback = null;
            _lastAnnouncementDimFeedback = null;
        }

        /// <summary>
        /// (Re)registers the remote-control keybinds as system-wide hotkeys when the
        /// setting is enabled and the platform supports it. Returns the count registered.
        /// </summary>
        internal int ApplyGlobalShortcuts(RemoteControlConfiguration remote)
        {
            try
            {
                var enabled = ConfigAccessor()?.GlobalKeyboardShortcuts == true;
                if (!enabled || !_hotkeys.IsSupported)
                {
                    _hotkeys.Clear();
                    RegisteredGlobalShortcuts = 0;
                    return 0;
                }

                RegisteredGlobalShortcuts = _hotkeys.RegisterAll(HotkeyActionMap.EnabledGestures(remote));
                return RegisteredGlobalShortcuts;
            }
            catch
            {
                RegisteredGlobalShortcuts = 0;
                return 0;
            }
        }

        private void OnGlobalHotkeyTriggered(object? sender, string actionId)
        {
            var action = HotkeyActionMap.ToRemoteAction(actionId);
            if (action is { } remoteAction)
                ExecuteAction(remoteAction, 0);
        }

        public void ApplyMidiDevices(int inputDevice, int outputDevice, bool enabled)
        {
            lock (_midiDeviceSwitchLock)
            {
                _feedbackTimer.Stop();
                try
                {
                    _midiService.IsEnabled = enabled;
                    if (!enabled)
                    {
                        _midiService.DisconnectDevices();
                        return;
                    }

                    _midiService.DisconnectDevices();

                    if (inputDevice >= 0)
                    {
                        _midiService.ConnectInput(inputDevice);
                    }

                    if (outputDevice >= 0)
                    {
                        _midiService.ConnectOutput(outputDevice);
                    }
                }
                finally
                {
                    _feedbackTimer.Start();
                }
            }
        }

        private void MidiService_MidiMessageReceived(object? sender, MidiMessage m)
        {
            var remote = RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl);
            if (!remote.MidiEnabled) return;

            int dataValue = 0;
            bool processed = false;

            if (m.Type == MidiMessageType.NoteOn && m.Data2 > 0)
            {
                dataValue = m.Data2;
                processed = MatchMidiMapping(remote.PlayPauseMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.PlayPause) || processed;
                processed = MatchMidiMapping(remote.SkipNextMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.SkipNext) || processed;
                processed = MatchMidiMapping(remote.PreviousMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.Previous) || processed;
                processed = MatchMidiMapping(remote.StopMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.Stop) || processed;
                processed = MatchMidiMapping(remote.VolumeMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.VolumeAbsolute) || processed;
                processed = MatchMidiMapping(remote.CrossfadeMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.CrossfadeAbsolute) || processed;
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.Announcement) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPlaySoundToggleMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.AnnouncementPlaySoundToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPushToTalkToggleMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.AnnouncementPushToTalkToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementDimDbMidi, "NoteOn", m.Channel, m.Data1, dataValue, RemoteAction.AnnouncementDimDbAbsolute) || processed;
            }
            else if (m.Type == MidiMessageType.NoteOn &&
                     m.Data2 == 0 &&
                     (ConfigAccessor()?.AnnouncementPushToTalk ?? true))
            {
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", m.Channel, m.Data1, 0, RemoteAction.AnnouncementRelease) || processed;
            }
            else if (m.Type == MidiMessageType.ControlChange)
            {
                dataValue = m.Data2;
                int controller = m.Data1;
                processed = MatchMidiMapping(remote.PlayPauseMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.PlayPause) || processed;
                processed = MatchMidiMapping(remote.SkipNextMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.SkipNext) || processed;
                processed = MatchMidiMapping(remote.PreviousMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.Previous) || processed;
                processed = MatchMidiMapping(remote.StopMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.Stop) || processed;
                processed = MatchMidiMapping(remote.VolumeMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.VolumeAbsolute) || processed;
                processed = MatchMidiMapping(remote.CrossfadeMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.CrossfadeAbsolute) || processed;
                processed = MatchMidiMapping(remote.AnnouncementMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.Announcement) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPlaySoundToggleMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.AnnouncementPlaySoundToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPushToTalkToggleMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.AnnouncementPushToTalkToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementDimDbMidi, "ControlChange", m.Channel, controller, dataValue, RemoteAction.AnnouncementDimDbAbsolute) || processed;
            }
            else if (m.Type == MidiMessageType.NoteOff &&
                     (ConfigAccessor()?.AnnouncementPushToTalk ?? true))
            {
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", m.Channel, m.Data1, 0, RemoteAction.AnnouncementRelease) || processed;
            }

            if (processed)
            {
                MidiActivity?.Invoke(this, $"MIDI input: {m.RawType} ch{m.Channel} {m.Data1}/{m.Data2}");
            }
        }

        internal bool MatchMidiMapping(MidiMapping? mapping, string type, int channel, int noteOrController, int value, RemoteAction action)
        {
            if (mapping == null || !mapping.IsConfigured) return false;
            if (!string.Equals(mapping.MessageType, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (mapping.Channel != channel || mapping.Note != noteOrController) return false;

            ExecuteAction(action, value);
            return true;
        }

        private void ExecuteAction(RemoteAction action, int dataValue)
        {
            var engine = _musicEngine;
            if (engine == null) return;

            void Run()
            {
                switch (action)
                {
                    case RemoteAction.PlayPause:
                        engine.TogglePlayPause();
                        break;
                    case RemoteAction.SkipNext:
                        engine.Next();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl).SkipNextMidi);
                        break;
                    case RemoteAction.Previous:
                        engine.Previous();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl).PreviousMidi);
                        break;
                    case RemoteAction.Stop:
                        engine.Stop();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl).StopMidi);
                        break;
                    case RemoteAction.VolumeUp:
                        engine.AdjustVolume(0.03);
                        break;
                    case RemoteAction.VolumeDown:
                        engine.AdjustVolume(-0.03);
                        break;
                    case RemoteAction.CrossfadeUp:
                        engine.AdjustCrossfade(0.25);
                        break;
                    case RemoteAction.CrossfadeDown:
                        engine.AdjustCrossfade(-0.25);
                        break;
                    case RemoteAction.VolumeAbsolute:
                        engine.SetVolumeFromMidi(dataValue / 127.0);
                        break;
                    case RemoteAction.CrossfadeAbsolute:
                        engine.SetCrossfadeFromMidi(dataValue / 127.0);
                        break;
                    case RemoteAction.Announcement:
                        _ = engine.RemoteAnnouncementActionAsync();
                        break;
                    case RemoteAction.AnnouncementRelease:
                        _ = engine.RemoteAnnouncementReleaseAsync();
                        break;
                    case RemoteAction.AnnouncementPlaySoundToggle:
                        engine.RemoteToggleAnnouncementPlaySound();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl).AnnouncementPlaySoundToggleMidi);
                        break;
                    case RemoteAction.AnnouncementPushToTalkToggle:
                        engine.RemoteToggleAnnouncementPushToTalk();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl).AnnouncementPushToTalkToggleMidi);
                        break;
                    case RemoteAction.AnnouncementDimDbUp:
                        engine.RemoteAdjustAnnouncementDimDb(1.0);
                        break;
                    case RemoteAction.AnnouncementDimDbDown:
                        engine.RemoteAdjustAnnouncementDimDb(-1.0);
                        break;
                    case RemoteAction.AnnouncementDimDbAbsolute:
                        engine.RemoteSetAnnouncementDimDbFromMidi(dataValue / 127.0);
                        break;
                }
            }

            // Marshal onto the UI thread (the engine provides the hook when the WPF host is up).
            var invoke = engine.InvokeOnUi;
            if (invoke != null) invoke(Run);
            else Run();
        }

        private void FlashButtonFeedback(MidiMapping? mapping)
        {
            if (mapping == null || !mapping.IsConfigured) return;
            SendButtonFeedback(mapping, mapping.VelocityPressed);

            var timer = new System.Timers.Timer(150) { AutoReset = false };
            timer.Elapsed += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                SendButtonFeedback(mapping, mapping.VelocityUnpressed);
            };
            timer.Start();
        }

        internal void PushFeedbackSnapshot()
        {
            if (_sendingFeedback) return;
            if (!_midiService.IsEnabled || _midiService.OutputDeviceNumber == null) return;

            var remote = RemoteControlConfiguration.Ensure(ConfigAccessor()?.RemoteControl);
            var engine = _musicEngine;
            if (engine == null) return;

            _sendingFeedback = true;
            try
            {
                int playValue = engine.IsPlaying ? (remote.PlayPauseMidi?.VelocityPressed ?? 127) : (remote.PlayPauseMidi?.VelocityUnpressed ?? 0);
                if (_lastPlayPauseFeedback != playValue)
                {
                    SendButtonFeedback(remote.PlayPauseMidi, playValue);
                    _lastPlayPauseFeedback = playValue;
                }

                if (remote.VolumeMidi != null && remote.VolumeMidi.IsConfigured && engine.CanControlVolume)
                {
                    int volumeValue = (int)Math.Round(Math.Clamp(engine.VolumeValue, 0.0, 1.0) * 127.0);
                    if (_lastVolumeFeedback != volumeValue)
                    {
                        SendSliderFeedback(remote.VolumeMidi, volumeValue);
                        _lastVolumeFeedback = volumeValue;
                    }
                }

                if (remote.CrossfadeMidi != null && remote.CrossfadeMidi.IsConfigured)
                {
                    int crossfadeValue = (int)Math.Round(engine.CrossfadeNormalized * 127.0);
                    if (_lastCrossfadeFeedback != crossfadeValue)
                    {
                        SendSliderFeedback(remote.CrossfadeMidi, crossfadeValue);
                        _lastCrossfadeFeedback = crossfadeValue;
                    }
                }

                if (remote.AnnouncementMidi != null && remote.AnnouncementMidi.IsConfigured)
                {
                    int announcementValue = engine.RemoteAnnouncementIsActive
                        ? (remote.AnnouncementMidi.VelocityPressed)
                        : (remote.AnnouncementMidi.VelocityUnpressed);
                    if (_lastAnnouncementFeedback != announcementValue)
                    {
                        SendButtonFeedback(remote.AnnouncementMidi, announcementValue);
                        _lastAnnouncementFeedback = announcementValue;
                    }
                }

                if (remote.AnnouncementDimDbMidi != null && remote.AnnouncementDimDbMidi.IsConfigured)
                {
                    int dimValue = (int)Math.Round(engine.RemoteAnnouncementDimNormalized * 127.0);
                    if (_lastAnnouncementDimFeedback != dimValue)
                    {
                        SendSliderFeedback(remote.AnnouncementDimDbMidi, dimValue);
                        _lastAnnouncementDimFeedback = dimValue;
                    }
                }
            }
            finally
            {
                _sendingFeedback = false;
            }
        }

        private void SendButtonFeedback(MidiMapping? mapping, int value)
        {
            if (mapping == null || !mapping.IsConfigured) return;
            value = Math.Clamp(value, 0, 127);

            if (string.Equals(mapping.MessageType, "ControlChange", StringComparison.OrdinalIgnoreCase))
            {
                _midiService.SendControlChange(mapping.Channel, mapping.Note, value);
            }
            else
            {
                if (value <= 0) _midiService.SendNoteOff(mapping.Channel, mapping.Note);
                else _midiService.SendNoteOn(mapping.Channel, mapping.Note, value);
            }
        }

        private void SendSliderFeedback(MidiMapping? mapping, int value)
        {
            if (mapping == null || !mapping.IsConfigured) return;
            value = Math.Clamp(value, 0, 127);

            if (string.Equals(mapping.MessageType, "ControlChange", StringComparison.OrdinalIgnoreCase))
            {
                _midiService.SendControlChange(mapping.Channel, mapping.Note, value);
            }
            else
            {
                _midiService.SendNoteOn(mapping.Channel, mapping.Note, value);
            }
        }

        public void Dispose()
        {
            _feedbackTimer.Stop();
            _feedbackTimer.Dispose();
            try { _hotkeys.Dispose(); } catch { }
            _midiService.Dispose();
        }

        internal enum RemoteAction
        {
            PlayPause,
            SkipNext,
            Previous,
            Stop,
            VolumeUp,
            VolumeDown,
            CrossfadeUp,
            CrossfadeDown,
            VolumeAbsolute,
            CrossfadeAbsolute,
            Announcement,
            AnnouncementRelease,
            AnnouncementPlaySoundToggle,
            AnnouncementPushToTalkToggle,
            AnnouncementDimDbUp,
            AnnouncementDimDbDown,
            AnnouncementDimDbAbsolute
        }
    }
}
