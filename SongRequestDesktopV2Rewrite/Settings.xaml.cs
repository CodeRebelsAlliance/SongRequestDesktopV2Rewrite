using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class Settings : Window
    {
        private bool _isTokenVisible;
        private bool _isSyncingTokenControls;
        private bool _isLoadingRemoteUi;
        private bool _isApplyingRemoteMidiDevices;
        private RemoteControlConfiguration _remoteControlConfig = new RemoteControlConfiguration();

        public Settings()
        {
            InitializeComponent();
            RemoteControlService.Instance.MidiActivity += RemoteControlService_MidiActivity;
            LoadCurrentConfig();
        }

        private T GetControl<T>(string name) where T : class
        {
            return FindName(name) as T;
        }

        private void SetTokenValue(string token)
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            _isSyncingTokenControls = true;
            if (tokenPasswordBox != null) tokenPasswordBox.Password = token ?? string.Empty;
            if (tokenRevealBox != null) tokenRevealBox.Text = token ?? string.Empty;
            _isSyncingTokenControls = false;
        }

        private string GetTokenValue()
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");

            if (_isTokenVisible)
            {
                return tokenRevealBox?.Text ?? tokenPasswordBox?.Password ?? string.Empty;
            }

            return tokenPasswordBox?.Password ?? tokenRevealBox?.Text ?? string.Empty;
        }

        private void UpdateTokenVisibility()
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            var toggleButton = GetControl<Button>("ToggleTokenVisibilityButton");

            if (tokenPasswordBox != null)
            {
                tokenPasswordBox.Visibility = _isTokenVisible ? Visibility.Collapsed : Visibility.Visible;
            }

            if (tokenRevealBox != null)
            {
                tokenRevealBox.Visibility = _isTokenVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (toggleButton != null)
            {
                toggleButton.Content = _isTokenVisible ? "🙈" : "👁";
            }
        }

        private void LoadCurrentConfig()
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return;

            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var requestUrlBox = GetControl<TextBox>("RequestUrlBox");
            var fullscreenCheckBox = GetControl<CheckBox>("PresentationFullscreenCheckBox");
            var normalizeVolumeCheckBox = GetControl<CheckBox>("NormalizeVolumeCheckBox");
            var autoEnqueueCheckBox = GetControl<CheckBox>("AutoEnqueueCheckBox");
            var captionFallbackCheckBox = GetControl<CheckBox>("CaptionFallbackCheckBox");
            var enableAnnouncementsCheckBox = GetControl<CheckBox>("EnableAnnouncementsCheckBox");

            if (fetchingBox != null) fetchingBox.Text = cfg.FetchingTimer.ToString();
            if (threadsBox != null) threadsBox.Text = cfg.Threads.ToString();
            SetTokenValue(cfg.BearerToken);
            if (addressBox != null) addressBox.Text = cfg.Address;
            if (sortingBox != null) sortingBox.Text = cfg.DefaultSorting;
            if (requestUrlBox != null) requestUrlBox.Text = cfg.RequestUrl;
            if (fullscreenCheckBox != null) fullscreenCheckBox.IsChecked = cfg.PresentationFullscreen;
            if (normalizeVolumeCheckBox != null) normalizeVolumeCheckBox.IsChecked = cfg.NormalizeVolume;
            if (autoEnqueueCheckBox != null) autoEnqueueCheckBox.IsChecked = cfg.AutoEnqueue;
            if (captionFallbackCheckBox != null) captionFallbackCheckBox.IsChecked = cfg.UseCaptionLyricsFallback;
            if (enableAnnouncementsCheckBox != null) enableAnnouncementsCheckBox.IsChecked = cfg.EnableAnnouncements;

            _remoteControlConfig = CloneRemoteControlConfig(RemoteControlConfiguration.Ensure(cfg.RemoteControl));
            LoadRemoteControlUi();

            _isTokenVisible = false;
            UpdateTokenVisibility();

            // Fetch server-side sendin allowed status
            _ = FetchSendinAllowedStatusAsync();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var requestUrlBox = GetControl<TextBox>("RequestUrlBox");
            var fullscreenCheckBox = GetControl<CheckBox>("PresentationFullscreenCheckBox");
            var normalizeVolumeCheckBox = GetControl<CheckBox>("NormalizeVolumeCheckBox");
            var autoEnqueueCheckBox = GetControl<CheckBox>("AutoEnqueueCheckBox");
            var captionFallbackCheckBox = GetControl<CheckBox>("CaptionFallbackCheckBox");
            var enableAnnouncementsCheckBox = GetControl<CheckBox>("EnableAnnouncementsCheckBox");
            var statusTb = GetControl<TextBlock>("StatusText");
            var tokenValue = GetTokenValue();

            try
            {
                int fetching = Math.Max(1, int.Parse(fetchingBox?.Text ?? "1"));
                int threads = Math.Max(1, int.Parse(threadsBox?.Text ?? "1"));

                ConfigService.Instance.Update(cfg =>
                {
                    cfg.FetchingTimer = fetching;
                    cfg.Threads = threads;
                    cfg.BearerToken = tokenValue;
                    cfg.Address = addressBox?.Text ?? string.Empty;
                    cfg.DefaultSorting = sortingBox?.Text ?? string.Empty;
                    cfg.RequestUrl = requestUrlBox?.Text ?? "https://example.com/request";
                    cfg.PresentationFullscreen = fullscreenCheckBox?.IsChecked ?? false;
                    cfg.NormalizeVolume = normalizeVolumeCheckBox?.IsChecked ?? false;
                    cfg.AutoEnqueue = autoEnqueueCheckBox?.IsChecked ?? false;
                    cfg.UseCaptionLyricsFallback = captionFallbackCheckBox?.IsChecked ?? true;
                    cfg.EnableAnnouncements = enableAnnouncementsCheckBox?.IsChecked ?? true;
                    cfg.RemoteControl = CloneRemoteControlConfig(_remoteControlConfig);

                    // If normalization is turned off, deactivate it
                    if (!cfg.NormalizeVolume)
                    {
                        cfg.NormalizationActive = false;
                    }
                });

                if (statusTb != null)
                {
                    statusTb.Text = "Saved.";
                    statusTb.Foreground = Brushes.LightGreen;
                }

                RemoteControlService.Instance.ApplyConfig();
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Error while saving: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private void ToggleTokenVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            _isTokenVisible = !_isTokenVisible;
            UpdateTokenVisibility();
        }

        private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingTokenControls) return;
            var tokenPasswordBox = sender as PasswordBox;
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            if (tokenPasswordBox == null || tokenRevealBox == null) return;

            _isSyncingTokenControls = true;
            tokenRevealBox.Text = tokenPasswordBox.Password;
            _isSyncingTokenControls = false;
        }

        private void TokenRevealBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingTokenControls) return;
            var tokenRevealBox = sender as TextBox;
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            if (tokenRevealBox == null || tokenPasswordBox == null) return;

            _isSyncingTokenControls = true;
            tokenPasswordBox.Password = tokenRevealBox.Text ?? string.Empty;
            _isSyncingTokenControls = false;
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Instance.Reload();
            LoadCurrentConfig();
            var statusTb = GetControl<TextBlock>("StatusText");
            if (statusTb != null)
            {
                statusTb.Text = "Reloaded from disk.";
                statusTb.Foreground = Brushes.LightGray;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            RemoteControlService.Instance.MidiActivity -= RemoteControlService_MidiActivity;
            base.OnClosed(e);
        }

        private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            // Find the main YoutubeForm window and clear its console
            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        rtb.Document.Blocks.Clear();
                        if (statusTb != null)
                        {
                            statusTb.Text = "Console cleared.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to clear.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void AppendConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            var appendBox = GetControl<TextBox>("AppendTextBox");
            var statusTb = GetControl<TextBlock>("StatusText");
            string text = appendBox?.Text ?? string.Empty;

            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        var paragraph = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text))
                        {
                            Margin = new Thickness(0),
                            Foreground = Brushes.White
                        };
                        rtb.Document.Blocks.Add(paragraph);
                        rtb.ScrollToEnd();
                        if (statusTb != null)
                        {
                            statusTb.Text = "Appended to console.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to append to.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void ApplyColorButton_Click(object sender, RoutedEventArgs e)
        {
            var combo = GetControl<ComboBox>("ConsoleColorCombo");
            var statusTb = GetControl<TextBlock>("StatusText");
            var sel = (combo?.SelectedItem as ComboBoxItem)?.Content as string;
            Brush brush = Brushes.White;
            switch (sel)
            {
                case "Green": brush = Brushes.Lime; break;
                case "Red": brush = Brushes.Red; break;
                case "White": brush = Brushes.White; break;
                default: brush = Brushes.White; break;
            }

            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        // Apply color to all existing paragraphs
                        foreach (var block in rtb.Document.Blocks)
                        {
                            if (block is System.Windows.Documents.Paragraph para)
                            {
                                para.Foreground = brush;
                            }
                        }
                        if (statusTb != null)
                        {
                            statusTb.Text = "Console color applied.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to set color.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void RegisterAuth_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var address = ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000";
                var url = $"https://schuelerapp.by-cra.net/sign-jwt?audience={Uri.EscapeDataString(address)}";
                var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                var statusTb = GetControl<TextBlock>("StatusText");
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to open browser: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private async System.Threading.Tasks.Task FetchSendinAllowedStatusAsync()
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            var sendinCheckBox = GetControl<CheckBox>("SendinAllowedCheckBox");

            try
            {
                var cfg = ConfigService.Instance.Current;
                if (cfg == null) return;

                var fts = new FetchingService(cfg.BearerToken);
                var result = await fts.SendRequest("get-sendin-allowed");

                // Parse the result - it's a list with a single boolean
                var jsonArray = Newtonsoft.Json.Linq.JArray.Parse(result);
                if (jsonArray.Count > 0)
                {
                    bool sendinAllowed = jsonArray[0].ToObject<bool>();
                    if (sendinCheckBox != null)
                    {
                        sendinCheckBox.IsChecked = sendinAllowed;
                    }

                    if (statusTb != null)
                    {
                        statusTb.Text = $"Server status: Sending {(sendinAllowed ? "Allowed" : "Disabled")}";
                        statusTb.Foreground = sendinAllowed ? Brushes.LightGreen : Brushes.OrangeRed;
                    }
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to fetch sendin status: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private async void ToggleSendinButton_Click(object sender, RoutedEventArgs e)
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            var toggleButton = GetControl<Button>("ToggleSendinButton");

            try
            {
                if (toggleButton != null) toggleButton.IsEnabled = false;

                var cfg = ConfigService.Instance.Current;
                if (cfg == null) return;

                var fts = new FetchingService(cfg.BearerToken);
                var result = await fts.SendRequest("toggle");

                // Refresh the status after toggle
                await FetchSendinAllowedStatusAsync();

                if (statusTb != null)
                {
                    statusTb.Text = "Toggled sendin status successfully";
                    statusTb.Foreground = Brushes.LightGreen;
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to toggle sendin status: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
            finally
            {
                if (toggleButton != null) toggleButton.IsEnabled = true;
            }
        }

        private void LoadRemoteControlUi()
        {
            _isLoadingRemoteUi = true;
            try
            {
                KeybindPlayPauseText.Text = FormatKeybind(_remoteControlConfig.PlayPauseKeybind);
                KeybindSkipNextText.Text = FormatKeybind(_remoteControlConfig.SkipNextKeybind);
                KeybindPreviousText.Text = FormatKeybind(_remoteControlConfig.PreviousKeybind);
                KeybindStopText.Text = FormatKeybind(_remoteControlConfig.StopKeybind);
                KeybindVolumeUpText.Text = FormatKeybind(_remoteControlConfig.VolumeUpKeybind);
                KeybindVolumeDownText.Text = FormatKeybind(_remoteControlConfig.VolumeDownKeybind);
                KeybindCrossfadeUpText.Text = FormatKeybind(_remoteControlConfig.CrossfadeUpKeybind);
                KeybindCrossfadeDownText.Text = FormatKeybind(_remoteControlConfig.CrossfadeDownKeybind);
                KeybindAnnouncementText.Text = FormatKeybind(_remoteControlConfig.AnnouncementKeybind);
                KeybindAnnouncementPlaySoundToggleText.Text = FormatKeybind(_remoteControlConfig.AnnouncementPlaySoundToggleKeybind);
                KeybindAnnouncementPushToTalkToggleText.Text = FormatKeybind(_remoteControlConfig.AnnouncementPushToTalkToggleKeybind);
                KeybindAnnouncementDimDbUpText.Text = FormatKeybind(_remoteControlConfig.AnnouncementDimDbUpKeybind);
                KeybindAnnouncementDimDbDownText.Text = FormatKeybind(_remoteControlConfig.AnnouncementDimDbDownKeybind);

                MidiPlayPauseText.Text = FormatMidi(_remoteControlConfig.PlayPauseMidi);
                MidiSkipNextText.Text = FormatMidi(_remoteControlConfig.SkipNextMidi);
                MidiPreviousText.Text = FormatMidi(_remoteControlConfig.PreviousMidi);
                MidiStopText.Text = FormatMidi(_remoteControlConfig.StopMidi);
                MidiVolumeText.Text = FormatMidi(_remoteControlConfig.VolumeMidi);
                MidiCrossfadeText.Text = FormatMidi(_remoteControlConfig.CrossfadeMidi);
                MidiAnnouncementText.Text = FormatMidi(_remoteControlConfig.AnnouncementMidi);
                MidiAnnouncementPlaySoundToggleText.Text = FormatMidi(_remoteControlConfig.AnnouncementPlaySoundToggleMidi);
                MidiAnnouncementPushToTalkToggleText.Text = FormatMidi(_remoteControlConfig.AnnouncementPushToTalkToggleMidi);
                MidiAnnouncementDimDbText.Text = FormatMidi(_remoteControlConfig.AnnouncementDimDbMidi);

                LoadRemoteMidiDevices();
            }
            finally
            {
                _isLoadingRemoteUi = false;
            }

            ApplyRemoteMidiPreviewDevices();
        }

        private void LoadRemoteMidiDevices()
        {
            if (RemoteMidiInputCombo == null || RemoteMidiOutputCombo == null || RemoteMidiEnabledCheckBox == null) return;

            RemoteMidiEnabledCheckBox.IsChecked = _remoteControlConfig.MidiEnabled;

            RemoteMidiInputCombo.Items.Clear();
            RemoteMidiInputCombo.Items.Add(new MidiDeviceInfo { DeviceNumber = -1, Name = "None", IsInput = true });
            foreach (var input in MidiService.GetInputDevices())
            {
                RemoteMidiInputCombo.Items.Add(input);
            }

            RemoteMidiOutputCombo.Items.Clear();
            RemoteMidiOutputCombo.Items.Add(new MidiDeviceInfo { DeviceNumber = -1, Name = "None", IsInput = false });
            foreach (var output in MidiService.GetOutputDevices())
            {
                RemoteMidiOutputCombo.Items.Add(output);
            }

            var selectedInput = RemoteMidiInputCombo.Items.Cast<MidiDeviceInfo>()
                .FirstOrDefault(d => d.DeviceNumber == _remoteControlConfig.MidiInputDevice);
            var selectedOutput = RemoteMidiOutputCombo.Items.Cast<MidiDeviceInfo>()
                .FirstOrDefault(d => d.DeviceNumber == _remoteControlConfig.MidiOutputDevice);

            RemoteMidiInputCombo.SelectedItem = selectedInput ?? RemoteMidiInputCombo.Items[0];
            RemoteMidiOutputCombo.SelectedItem = selectedOutput ?? RemoteMidiOutputCombo.Items[0];
            UpdateRemoteMidiStatusText();
        }

        private void UpdateRemoteMidiStatusText()
        {
            if (RemoteMidiStatusText == null) return;
            bool enabled = _remoteControlConfig.MidiEnabled;
            if (!enabled)
            {
                RemoteMidiStatusText.Text = "MIDI remote control is disabled.";
                return;
            }

            bool hasInput = _remoteControlConfig.MidiInputDevice >= 0;
            bool hasOutput = _remoteControlConfig.MidiOutputDevice >= 0;
            if (hasInput && hasOutput) RemoteMidiStatusText.Text = "MIDI input and output connected.";
            else if (hasInput) RemoteMidiStatusText.Text = "MIDI input connected (no output feedback device selected).";
            else if (hasOutput) RemoteMidiStatusText.Text = "MIDI output connected (no input device selected).";
            else RemoteMidiStatusText.Text = "No MIDI devices selected.";
        }

        private void RemoteControlService_MidiActivity(object? sender, string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (RemoteMidiLastMessageText != null)
                {
                    RemoteMidiLastMessageText.Text = message;
                }
            });
        }

        private void ApplyRemoteMidiPreviewDevices()
        {
            if (_isApplyingRemoteMidiDevices) return;
            _isApplyingRemoteMidiDevices = true;
            try
            {
                RemoteControlService.Instance.ApplyMidiDevices(
                    _remoteControlConfig.MidiInputDevice,
                    _remoteControlConfig.MidiOutputDevice,
                    _remoteControlConfig.MidiEnabled);
            }
            finally
            {
                _isApplyingRemoteMidiDevices = false;
            }
            UpdateRemoteMidiStatusText();
        }

        private void RemoteMidiDevice_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoadingRemoteUi) return;

            _remoteControlConfig.MidiEnabled = RemoteMidiEnabledCheckBox?.IsChecked == true;
            _remoteControlConfig.MidiInputDevice = (RemoteMidiInputCombo?.SelectedItem as MidiDeviceInfo)?.DeviceNumber ?? -1;
            _remoteControlConfig.MidiOutputDevice = (RemoteMidiOutputCombo?.SelectedItem as MidiDeviceInfo)?.DeviceNumber ?? -1;
            ApplyRemoteMidiPreviewDevices();
        }

        private void RemoteMidiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RemoteMidiDevice_SelectionChanged(sender, e);
        }

        private void AssignKeybindButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string action) return;

            var target = GetKeybindByAction(action);
            if (target == null) return;

            var dialog = new KeybindCaptureDialog(target.Gesture) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                target.Enabled = true;
                target.Gesture = dialog.SelectedGesture;
                LoadRemoteControlUi();
            }
        }

        private void ClearKeybindButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string action) return;

            var target = GetKeybindByAction(action);
            if (target == null) return;
            target.Enabled = false;
            target.Gesture = string.Empty;
            LoadRemoteControlUi();
        }

        private void AssignMidiButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string action) return;

            _remoteControlConfig.MidiEnabled = RemoteMidiEnabledCheckBox?.IsChecked == true;
            _remoteControlConfig.MidiInputDevice = (RemoteMidiInputCombo?.SelectedItem as MidiDeviceInfo)?.DeviceNumber ?? -1;
            _remoteControlConfig.MidiOutputDevice = (RemoteMidiOutputCombo?.SelectedItem as MidiDeviceInfo)?.DeviceNumber ?? -1;
            ApplyRemoteMidiPreviewDevices();

            if (_remoteControlConfig.MidiInputDevice < 0)
            {
                MessageBox.Show("Select a MIDI input device first.", "MIDI", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var existing = GetMidiMappingByAction(action);
            var dialog = new MidiAssignDialog(RemoteControlService.Instance.MidiService, existing) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                SetMidiMappingByAction(action, dialog.Result);
                LoadRemoteControlUi();
            }
        }

        private void ClearMidiButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string action) return;
            SetMidiMappingByAction(action, null);
            LoadRemoteControlUi();
        }

        private KeyboardShortcutConfig? GetKeybindByAction(string action)
        {
            return action switch
            {
                "PlayPause" => _remoteControlConfig.PlayPauseKeybind,
                "SkipNext" => _remoteControlConfig.SkipNextKeybind,
                "Previous" => _remoteControlConfig.PreviousKeybind,
                "Stop" => _remoteControlConfig.StopKeybind,
                "VolumeUp" => _remoteControlConfig.VolumeUpKeybind,
                "VolumeDown" => _remoteControlConfig.VolumeDownKeybind,
                "CrossfadeUp" => _remoteControlConfig.CrossfadeUpKeybind,
                "CrossfadeDown" => _remoteControlConfig.CrossfadeDownKeybind,
                "Announcement" => _remoteControlConfig.AnnouncementKeybind,
                "AnnouncementPlaySoundToggle" => _remoteControlConfig.AnnouncementPlaySoundToggleKeybind,
                "AnnouncementPushToTalkToggle" => _remoteControlConfig.AnnouncementPushToTalkToggleKeybind,
                "AnnouncementDimDbUp" => _remoteControlConfig.AnnouncementDimDbUpKeybind,
                "AnnouncementDimDbDown" => _remoteControlConfig.AnnouncementDimDbDownKeybind,
                _ => null
            };
        }

        private MidiMapping? GetMidiMappingByAction(string action)
        {
            return action switch
            {
                "PlayPause" => _remoteControlConfig.PlayPauseMidi,
                "SkipNext" => _remoteControlConfig.SkipNextMidi,
                "Previous" => _remoteControlConfig.PreviousMidi,
                "Stop" => _remoteControlConfig.StopMidi,
                "Volume" => _remoteControlConfig.VolumeMidi,
                "Crossfade" => _remoteControlConfig.CrossfadeMidi,
                "Announcement" => _remoteControlConfig.AnnouncementMidi,
                "AnnouncementPlaySoundToggle" => _remoteControlConfig.AnnouncementPlaySoundToggleMidi,
                "AnnouncementPushToTalkToggle" => _remoteControlConfig.AnnouncementPushToTalkToggleMidi,
                "AnnouncementDimDb" => _remoteControlConfig.AnnouncementDimDbMidi,
                _ => null
            };
        }

        private void SetMidiMappingByAction(string action, MidiMapping? mapping)
        {
            switch (action)
            {
                case "PlayPause": _remoteControlConfig.PlayPauseMidi = CloneMidiMapping(mapping); break;
                case "SkipNext": _remoteControlConfig.SkipNextMidi = CloneMidiMapping(mapping); break;
                case "Previous": _remoteControlConfig.PreviousMidi = CloneMidiMapping(mapping); break;
                case "Stop": _remoteControlConfig.StopMidi = CloneMidiMapping(mapping); break;
                case "Volume": _remoteControlConfig.VolumeMidi = CloneMidiMapping(mapping); break;
                case "Crossfade": _remoteControlConfig.CrossfadeMidi = CloneMidiMapping(mapping); break;
                case "Announcement": _remoteControlConfig.AnnouncementMidi = CloneMidiMapping(mapping); break;
                case "AnnouncementPlaySoundToggle": _remoteControlConfig.AnnouncementPlaySoundToggleMidi = CloneMidiMapping(mapping); break;
                case "AnnouncementPushToTalkToggle": _remoteControlConfig.AnnouncementPushToTalkToggleMidi = CloneMidiMapping(mapping); break;
                case "AnnouncementDimDb": _remoteControlConfig.AnnouncementDimDbMidi = CloneMidiMapping(mapping); break;
            }
        }

        private static string FormatKeybind(KeyboardShortcutConfig keybind)
        {
            if (keybind == null || !keybind.Enabled || string.IsNullOrWhiteSpace(keybind.Gesture))
            {
                return "Not assigned";
            }
            return keybind.Gesture;
        }

        private static string FormatMidi(MidiMapping? mapping)
        {
            if (mapping == null || !mapping.IsConfigured) return "Not assigned";
            string unit = string.Equals(mapping.MessageType, "ControlChange", StringComparison.OrdinalIgnoreCase) ? "CC" : "Note";
            return $"{mapping.MessageType} | Ch {mapping.Channel} | {unit} {mapping.Note}";
        }

        private static KeyboardShortcutConfig CloneKeybind(KeyboardShortcutConfig source)
        {
            return new KeyboardShortcutConfig
            {
                Enabled = source?.Enabled == true,
                Gesture = source?.Gesture ?? string.Empty
            };
        }

        private static MidiMapping? CloneMidiMapping(MidiMapping? source)
        {
            if (source == null) return null;
            return new MidiMapping
            {
                Channel = source.Channel,
                Note = source.Note,
                MessageType = source.MessageType,
                VelocityPressed = source.VelocityPressed,
                VelocityUnpressed = source.VelocityUnpressed
            };
        }

        private static RemoteControlConfiguration CloneRemoteControlConfig(RemoteControlConfiguration source)
        {
            return new RemoteControlConfiguration
            {
                PlayPauseKeybind = CloneKeybind(source.PlayPauseKeybind),
                SkipNextKeybind = CloneKeybind(source.SkipNextKeybind),
                PreviousKeybind = CloneKeybind(source.PreviousKeybind),
                StopKeybind = CloneKeybind(source.StopKeybind),
                VolumeUpKeybind = CloneKeybind(source.VolumeUpKeybind),
                VolumeDownKeybind = CloneKeybind(source.VolumeDownKeybind),
                CrossfadeUpKeybind = CloneKeybind(source.CrossfadeUpKeybind),
                CrossfadeDownKeybind = CloneKeybind(source.CrossfadeDownKeybind),
                AnnouncementKeybind = CloneKeybind(source.AnnouncementKeybind),
                AnnouncementPlaySoundToggleKeybind = CloneKeybind(source.AnnouncementPlaySoundToggleKeybind),
                AnnouncementPushToTalkToggleKeybind = CloneKeybind(source.AnnouncementPushToTalkToggleKeybind),
                AnnouncementDimDbUpKeybind = CloneKeybind(source.AnnouncementDimDbUpKeybind),
                AnnouncementDimDbDownKeybind = CloneKeybind(source.AnnouncementDimDbDownKeybind),
                MidiEnabled = source.MidiEnabled,
                MidiInputDevice = source.MidiInputDevice,
                MidiOutputDevice = source.MidiOutputDevice,
                PlayPauseMidi = CloneMidiMapping(source.PlayPauseMidi),
                SkipNextMidi = CloneMidiMapping(source.SkipNextMidi),
                PreviousMidi = CloneMidiMapping(source.PreviousMidi),
                StopMidi = CloneMidiMapping(source.StopMidi),
                VolumeMidi = CloneMidiMapping(source.VolumeMidi),
                CrossfadeMidi = CloneMidiMapping(source.CrossfadeMidi),
                AnnouncementMidi = CloneMidiMapping(source.AnnouncementMidi),
                AnnouncementPlaySoundToggleMidi = CloneMidiMapping(source.AnnouncementPlaySoundToggleMidi),
                AnnouncementPushToTalkToggleMidi = CloneMidiMapping(source.AnnouncementPushToTalkToggleMidi),
                AnnouncementDimDbMidi = CloneMidiMapping(source.AnnouncementDimDbMidi)
            };
        }
    }
}
