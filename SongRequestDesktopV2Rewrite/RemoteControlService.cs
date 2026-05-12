using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Midi;

namespace SongRequestDesktopV2Rewrite
{
    public sealed class RemoteControlService : IDisposable
    {
        private static readonly Lazy<RemoteControlService> _instance = new(() => new RemoteControlService());
        public static RemoteControlService Instance => _instance.Value;

        private readonly MidiService _midiService = new MidiService();
        private readonly DispatcherTimer _feedbackTimer;
        private MusicPlayer? _musicPlayer;
        private bool _initialized;
        private bool _suppressKeyboard;
        private bool _sendingFeedback;
        private readonly object _midiDeviceSwitchLock = new object();
        private int? _lastPlayPauseFeedback;
        private int? _lastVolumeFeedback;
        private int? _lastCrossfadeFeedback;
        private int? _lastAnnouncementFeedback;
        private int? _lastAnnouncementDimFeedback;

        public MidiService MidiService => _midiService;
        public event EventHandler<string>? MidiActivity;

        private RemoteControlService()
        {
            _feedbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _feedbackTimer.Tick += (_, _) => PushFeedbackSnapshot();
            _midiService.MidiMessageReceived += MidiService_MidiMessageReceived;
            _midiService.ErrorOccurred += (_, message) => MidiActivity?.Invoke(this, $"MIDI error: {message}");
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            ApplyConfig();
            _feedbackTimer.Start();
        }

        public void RegisterMusicPlayer(MusicPlayer musicPlayer)
        {
            _musicPlayer = musicPlayer;
        }

        public void ApplyConfig()
        {
            var cfg = RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl);
            ApplyMidiDevices(cfg.MidiInputDevice, cfg.MidiOutputDevice, cfg.MidiEnabled);
            _lastPlayPauseFeedback = null;
            _lastVolumeFeedback = null;
            _lastCrossfadeFeedback = null;
            _lastAnnouncementFeedback = null;
            _lastAnnouncementDimFeedback = null;
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

        private void InputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (_suppressKeyboard) return;
            if (e.StagingItem.Input is not KeyEventArgs keyEvent) return;
            bool isKeyDown = keyEvent.RoutedEvent == Keyboard.KeyDownEvent;
            bool isKeyUp = keyEvent.RoutedEvent == Keyboard.KeyUpEvent;
            if (!isKeyDown && !isKeyUp) return;
            if (isKeyDown && keyEvent.IsRepeat) return;

            var activeWindow = Application.Current?.Windows.Count > 0 ? Application.Current.Windows.OfType<Window>() : null;
            foreach (var window in activeWindow ?? Array.Empty<Window>())
            {
                if (window.IsActive && window is KeybindCaptureDialog)
                {
                    return;
                }
            }

            var remote = RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl);

            try
            {
                _suppressKeyboard = true;
                if (isKeyDown)
                {
                    if (TryMatchAndExecute(remote.PlayPauseKeybind, keyEvent, RemoteAction.PlayPause)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.SkipNextKeybind, keyEvent, RemoteAction.SkipNext)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.PreviousKeybind, keyEvent, RemoteAction.Previous)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.StopKeybind, keyEvent, RemoteAction.Stop)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.VolumeUpKeybind, keyEvent, RemoteAction.VolumeUp)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.VolumeDownKeybind, keyEvent, RemoteAction.VolumeDown)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.CrossfadeUpKeybind, keyEvent, RemoteAction.CrossfadeUp)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.CrossfadeDownKeybind, keyEvent, RemoteAction.CrossfadeDown)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.AnnouncementKeybind, keyEvent, RemoteAction.Announcement)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.AnnouncementPlaySoundToggleKeybind, keyEvent, RemoteAction.AnnouncementPlaySoundToggle)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.AnnouncementPushToTalkToggleKeybind, keyEvent, RemoteAction.AnnouncementPushToTalkToggle)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.AnnouncementDimDbUpKeybind, keyEvent, RemoteAction.AnnouncementDimDbUp)) { keyEvent.Handled = true; return; }
                    if (TryMatchAndExecute(remote.AnnouncementDimDbDownKeybind, keyEvent, RemoteAction.AnnouncementDimDbDown)) { keyEvent.Handled = true; return; }
                }

                if (isKeyUp && (ConfigService.Instance.Current?.AnnouncementPushToTalk ?? true))
                {
                    if (KeyboardShortcutHelper.IsShortcutMatch(remote.AnnouncementKeybind, keyEvent))
                    {
                        ExecuteAction(RemoteAction.AnnouncementRelease, 0);
                        keyEvent.Handled = true;
                        return;
                    }
                }
            }
            finally
            {
                _suppressKeyboard = false;
            }
        }

        private bool TryMatchAndExecute(KeyboardShortcutConfig shortcut, KeyEventArgs keyEvent, RemoteAction action)
        {
            if (!KeyboardShortcutHelper.IsShortcutMatch(shortcut, keyEvent)) return false;
            ExecuteAction(action, 0);
            return true;
        }

        private void MidiService_MidiMessageReceived(object? sender, MidiInMessageEventArgs e)
        {
            var remote = RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl);
            if (!remote.MidiEnabled) return;

            int dataValue = 0;
            bool processed = false;

            if (e.MidiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0)
            {
                dataValue = noteOn.Velocity;
                processed = MatchMidiMapping(remote.PlayPauseMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.PlayPause) || processed;
                processed = MatchMidiMapping(remote.SkipNextMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.SkipNext) || processed;
                processed = MatchMidiMapping(remote.PreviousMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.Previous) || processed;
                processed = MatchMidiMapping(remote.StopMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.Stop) || processed;
                processed = MatchMidiMapping(remote.VolumeMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.VolumeAbsolute) || processed;
                processed = MatchMidiMapping(remote.CrossfadeMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.CrossfadeAbsolute) || processed;
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.Announcement) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPlaySoundToggleMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.AnnouncementPlaySoundToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPushToTalkToggleMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.AnnouncementPushToTalkToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementDimDbMidi, "NoteOn", noteOn.Channel, noteOn.NoteNumber, dataValue, RemoteAction.AnnouncementDimDbAbsolute) || processed;
            }
            else if (e.MidiEvent is NoteOnEvent noteOnZero &&
                     noteOnZero.Velocity == 0 &&
                     (ConfigService.Instance.Current?.AnnouncementPushToTalk ?? true))
            {
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", noteOnZero.Channel, noteOnZero.NoteNumber, 0, RemoteAction.AnnouncementRelease) || processed;
            }
            else if (e.MidiEvent is ControlChangeEvent cc)
            {
                dataValue = cc.ControllerValue;
                int controller = (int)cc.Controller;
                processed = MatchMidiMapping(remote.PlayPauseMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.PlayPause) || processed;
                processed = MatchMidiMapping(remote.SkipNextMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.SkipNext) || processed;
                processed = MatchMidiMapping(remote.PreviousMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.Previous) || processed;
                processed = MatchMidiMapping(remote.StopMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.Stop) || processed;
                processed = MatchMidiMapping(remote.VolumeMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.VolumeAbsolute) || processed;
                processed = MatchMidiMapping(remote.CrossfadeMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.CrossfadeAbsolute) || processed;
                processed = MatchMidiMapping(remote.AnnouncementMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.Announcement) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPlaySoundToggleMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.AnnouncementPlaySoundToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementPushToTalkToggleMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.AnnouncementPushToTalkToggle) || processed;
                processed = MatchMidiMapping(remote.AnnouncementDimDbMidi, "ControlChange", cc.Channel, controller, dataValue, RemoteAction.AnnouncementDimDbAbsolute) || processed;
            }
            else if (e.MidiEvent is NoteEvent noteEvent &&
                     noteEvent.CommandCode == MidiCommandCode.NoteOff &&
                     (ConfigService.Instance.Current?.AnnouncementPushToTalk ?? true))
            {
                processed = MatchMidiMapping(remote.AnnouncementMidi, "NoteOn", noteEvent.Channel, noteEvent.NoteNumber, 0, RemoteAction.AnnouncementRelease) || processed;
            }

            if (processed)
            {
                MidiActivity?.Invoke(this, $"MIDI input: {e.MidiEvent}");
            }
        }

        private bool MatchMidiMapping(MidiMapping? mapping, string type, int channel, int noteOrController, int value, RemoteAction action)
        {
            if (mapping == null || !mapping.IsConfigured) return false;
            if (!string.Equals(mapping.MessageType, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (mapping.Channel != channel || mapping.Note != noteOrController) return false;

            ExecuteAction(action, value);
            return true;
        }

        private void ExecuteAction(RemoteAction action, int dataValue)
        {
            var player = _musicPlayer;
            if (player == null) return;

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                switch (action)
                {
                    case RemoteAction.PlayPause:
                        player.RemoteTogglePlayPause();
                        break;
                    case RemoteAction.SkipNext:
                        player.RemoteSkipNext();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl).SkipNextMidi);
                        break;
                    case RemoteAction.Previous:
                        player.RemotePrevious();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl).PreviousMidi);
                        break;
                    case RemoteAction.Stop:
                        player.RemoteStop();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl).StopMidi);
                        break;
                    case RemoteAction.VolumeUp:
                        player.RemoteAdjustVolume(0.03);
                        break;
                    case RemoteAction.VolumeDown:
                        player.RemoteAdjustVolume(-0.03);
                        break;
                    case RemoteAction.CrossfadeUp:
                        player.RemoteAdjustCrossfade(0.25);
                        break;
                    case RemoteAction.CrossfadeDown:
                        player.RemoteAdjustCrossfade(-0.25);
                        break;
                    case RemoteAction.VolumeAbsolute:
                        player.RemoteSetVolumeFromMidi(dataValue / 127.0);
                        break;
                    case RemoteAction.CrossfadeAbsolute:
                        player.RemoteSetCrossfadeFromMidi(dataValue / 127.0);
                        break;
                    case RemoteAction.Announcement:
                        _ = player.RemoteAnnouncementActionAsync();
                        break;
                    case RemoteAction.AnnouncementRelease:
                        _ = player.RemoteAnnouncementReleaseAsync();
                        break;
                    case RemoteAction.AnnouncementPlaySoundToggle:
                        player.RemoteToggleAnnouncementPlaySound();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl).AnnouncementPlaySoundToggleMidi);
                        break;
                    case RemoteAction.AnnouncementPushToTalkToggle:
                        player.RemoteToggleAnnouncementPushToTalk();
                        FlashButtonFeedback(RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl).AnnouncementPushToTalkToggleMidi);
                        break;
                    case RemoteAction.AnnouncementDimDbUp:
                        player.RemoteAdjustAnnouncementDimDb(1.0);
                        break;
                    case RemoteAction.AnnouncementDimDbDown:
                        player.RemoteAdjustAnnouncementDimDb(-1.0);
                        break;
                    case RemoteAction.AnnouncementDimDbAbsolute:
                        player.RemoteSetAnnouncementDimDbFromMidi(dataValue / 127.0);
                        break;
                }
            });
        }

        private void FlashButtonFeedback(MidiMapping? mapping)
        {
            if (mapping == null || !mapping.IsConfigured) return;
            SendButtonFeedback(mapping, mapping.VelocityPressed);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SendButtonFeedback(mapping, mapping.VelocityUnpressed);
            };
            timer.Start();
        }

        private void PushFeedbackSnapshot()
        {
            if (_sendingFeedback) return;
            if (!_midiService.IsEnabled || _midiService.OutputDeviceNumber == null) return;

            var remote = RemoteControlConfiguration.Ensure(ConfigService.Instance.Current?.RemoteControl);
            var player = _musicPlayer;
            if (player == null) return;

            _sendingFeedback = true;
            try
            {
                int playValue = player.RemoteIsPlaying ? (remote.PlayPauseMidi?.VelocityPressed ?? 127) : (remote.PlayPauseMidi?.VelocityUnpressed ?? 0);
                if (_lastPlayPauseFeedback != playValue)
                {
                    SendButtonFeedback(remote.PlayPauseMidi, playValue);
                    _lastPlayPauseFeedback = playValue;
                }

                if (remote.VolumeMidi != null && remote.VolumeMidi.IsConfigured && player.RemoteCanControlVolume)
                {
                    int volumeValue = (int)Math.Round(Math.Clamp(player.RemoteVolumeValue, 0.0, 1.0) * 127.0);
                    if (_lastVolumeFeedback != volumeValue)
                    {
                        SendSliderFeedback(remote.VolumeMidi, volumeValue);
                        _lastVolumeFeedback = volumeValue;
                    }
                }

                if (remote.CrossfadeMidi != null && remote.CrossfadeMidi.IsConfigured)
                {
                    int crossfadeValue = (int)Math.Round(player.RemoteCrossfadeNormalized * 127.0);
                    if (_lastCrossfadeFeedback != crossfadeValue)
                    {
                        SendSliderFeedback(remote.CrossfadeMidi, crossfadeValue);
                        _lastCrossfadeFeedback = crossfadeValue;
                    }
                }

                if (remote.AnnouncementMidi != null && remote.AnnouncementMidi.IsConfigured)
                {
                    int announcementValue = player.RemoteAnnouncementIsActive
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
                    int dimValue = (int)Math.Round(player.RemoteAnnouncementDimNormalized * 127.0);
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
            if (_initialized)
            {
                InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
            }
            _midiService.Dispose();
        }

        private enum RemoteAction
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
