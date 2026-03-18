using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite
{
    public partial class ButtonEditDialog : Window
    {
        private MidiMapping? _midiMapping;

        private static readonly string[] PredefinedColors =
        {
            "#2A2A2A",
            "#FF5733",
            "#33FF57",
            "#3357FF",
            "#FF33F5",
            "#F5FF33",
            "#33FFF5",
            "#FF8C33",
            "#8C33FF",
            "#FF3383"
        };

        public string ButtonName { get; private set; }
        public string ButtonColor { get; private set; }
        public string ButtonIcon { get; private set; }
        public bool FadeIn { get; private set; }
        public bool FadeOut { get; private set; }
        public string RepeatMode { get; private set; }
        public string SoundFilePath { get; private set; }
        public float ButtonVolume { get; private set; }
        public int? CustomAudioDeviceNumber { get; private set; }
        public KeyboardShortcutConfig ButtonShortcut { get; private set; }

        public ButtonEditDialog(SoundboardButton button)
        {
            InitializeComponent();

            ButtonName = button.Name;
            ButtonColor = button.Color;
            ButtonIcon = button.Icon;
            FadeIn = button.FadeIn;
            FadeOut = button.FadeOut;
            RepeatMode = button.RepeatMode;
            ButtonVolume = button.Volume;
            CustomAudioDeviceNumber = button.CustomOutputDeviceNumber;
            ButtonShortcut = new KeyboardShortcutConfig
            {
                Enabled = button.KeyboardShortcut?.Enabled ?? false,
                Gesture = button.KeyboardShortcut?.Gesture ?? string.Empty
            };
            _midiMapping = button.MidiMapping;

            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();
            SoundFilePath = string.IsNullOrEmpty(button.SoundFile)
                ? string.Empty
                : Path.Combine(soundboardFolder, button.SoundFile);

            InitializeUiFromData();
        }

        private void InitializeUiFromData()
        {
            _nameTextBox.Text = ButtonName;
            _iconTextBox.Text = ButtonIcon;
            _soundFileTextBox.Text = Path.GetFileName(SoundFilePath);
            _fadeInCheckBox.IsChecked = FadeIn;
            _fadeOutCheckBox.IsChecked = FadeOut;
            _volumeSlider.Value = ButtonVolume * 100;
            _volumeLabel.Text = $"{(int)_volumeSlider.Value}%";

            InitializeAudioDevices();
            UpdateShortcutUi();
            InitializePredefinedColorButtons();

            _customColorTextBox.Text = IsCustomColor(ButtonColor) ? ButtonColor : "#";
            if (IsCustomColor(ButtonColor))
            {
                try
                {
                    _customColorPreview.Background = (Brush)new BrushConverter().ConvertFromString(ButtonColor);
                }
                catch
                {
                    _customColorPreview.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                }
            }

            foreach (ComboBoxItem item in _repeatModeCombo.Items)
            {
                if ((string)item.Tag == RepeatMode)
                {
                    _repeatModeCombo.SelectedItem = item;
                    break;
                }
            }

            if (_repeatModeCombo.SelectedItem == null)
            {
                _repeatModeCombo.SelectedIndex = 0;
            }

            if (_midiMapping != null && _midiMapping.IsConfigured)
            {
                _midiStatusText.Text = $"✓ Mapped to Channel {_midiMapping.Channel}, {_midiMapping.MessageType} {_midiMapping.Note}\n" +
                                       $"Pressed: {_midiMapping.VelocityPressed}, Unpressed: {_midiMapping.VelocityUnpressed}";
                _midiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else
            {
                _midiStatusText.Text = "Not mapped - Use 'Assign' mode in main window to map a MIDI control";
                _midiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153));
            }
        }

        private void UpdateShortcutUi()
        {
            _buttonShortcutEnabledCheckBox.IsChecked = ButtonShortcut.Enabled;
            _buttonShortcutText.Text = string.IsNullOrWhiteSpace(ButtonShortcut.Gesture)
                ? "(not set)"
                : ButtonShortcut.Gesture;
        }

        private void InitializeAudioDevices()
        {
            _customAudioDevice.Items.Clear();

            _customAudioDevice.Items.Add(new ComboBoxItem
            {
                Content = "Use Global Device",
                Tag = null
            });

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var capabilities = WaveOut.GetCapabilities(i);
                _customAudioDevice.Items.Add(new ComboBoxItem
                {
                    Content = capabilities.ProductName,
                    Tag = i
                });
            }

            if (CustomAudioDeviceNumber.HasValue)
            {
                foreach (ComboBoxItem item in _customAudioDevice.Items)
                {
                    if (item.Tag is int device && device == CustomAudioDeviceNumber.Value)
                    {
                        _customAudioDevice.SelectedItem = item;
                        break;
                    }
                }
            }

            if (_customAudioDevice.SelectedItem == null)
            {
                _customAudioDevice.SelectedIndex = 0;
            }
        }

        private void InitializePredefinedColorButtons()
        {
            _colorPanel.Children.Clear();

            foreach (var color in PredefinedColors)
            {
                var colorButton = new Button
                {
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(2),
                    Background = (Brush)new BrushConverter().ConvertFromString(color),
                    BorderThickness = new Thickness(2),
                    BorderBrush = color == ButtonColor
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(64, 64, 64)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = color
                };

                colorButton.Click += ColorButton_Click;
                _colorPanel.Children.Add(colorButton);
            }
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            ButtonColor = (string)button.Tag;

            foreach (Button colorBtn in _colorPanel.Children.OfType<Button>())
            {
                colorBtn.BorderBrush = (string)colorBtn.Tag == ButtonColor
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(64, 64, 64));
            }

            _customColorTextBox.Text = "#";
        }

        private void CustomColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_customColorTextBox == null || _customColorPreview == null)
            {
                return;
            }

            var hexColor = _customColorTextBox.Text;

            if (System.Text.RegularExpressions.Regex.IsMatch(hexColor, @"^#[0-9A-Fa-f]{6}$"))
            {
                try
                {
                    var brush = (Brush)new BrushConverter().ConvertFromString(hexColor);
                    _customColorPreview.Background = brush;
                    ButtonColor = hexColor.ToUpperInvariant();

                    if (_colorPanel != null)
                    {
                        foreach (Button colorBtn in _colorPanel.Children.OfType<Button>())
                        {
                            colorBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));
                        }
                    }
                }
                catch
                {
                    _customColorPreview.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                }
            }
            else
            {
                _customColorPreview.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            }
        }

        private bool IsCustomColor(string color)
        {
            return !PredefinedColors.Contains(color?.ToUpperInvariant());
        }

        private void BrowseSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var soundboardFolder = SoundboardConfiguration.GetSoundboardFolder();

            var dialog = new OpenFileDialog
            {
                Title = "Select Sound File",
                Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma|All Files|*.*",
                InitialDirectory = soundboardFolder
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var selectedFile = dialog.FileName;
            var fileName = Path.GetFileName(selectedFile);
            var destPath = Path.Combine(soundboardFolder, fileName);

            if (!File.Exists(destPath))
            {
                try
                {
                    File.Copy(selectedFile, destPath, false);
                    System.Diagnostics.Debug.WriteLine("✓ Copied new sound file to soundboard folder");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error copying file:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            SoundFilePath = destPath;
            _soundFileTextBox.Text = fileName;
        }

        private void SetButtonShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new KeybindCaptureDialog(ButtonShortcut.Gesture)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                ButtonShortcut.Gesture = dialog.SelectedGesture;
                if (!string.IsNullOrWhiteSpace(ButtonShortcut.Gesture))
                {
                    ButtonShortcut.Enabled = true;
                }

                UpdateShortcutUi();
            }
        }

        private void ClearButtonShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            ButtonShortcut.Gesture = string.Empty;
            ButtonShortcut.Enabled = false;
            UpdateShortcutUi();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
            {
                MessageBox.Show("Please enter a button name.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(SoundFilePath))
            {
                MessageBox.Show("Please select a sound file.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ButtonName = _nameTextBox.Text;
            ButtonIcon = _iconTextBox.Text;
            FadeIn = _fadeInCheckBox.IsChecked ?? false;
            FadeOut = _fadeOutCheckBox.IsChecked ?? false;
            ButtonVolume = (float)(_volumeSlider.Value / 100.0);

            if (_repeatModeCombo.SelectedItem is ComboBoxItem selectedItem)
            {
                RepeatMode = (string)selectedItem.Tag;
            }

            if (_customAudioDevice.SelectedItem is ComboBoxItem deviceItem)
            {
                CustomAudioDeviceNumber = deviceItem.Tag is int deviceNumber ? deviceNumber : null;
            }
            else
            {
                CustomAudioDeviceNumber = null;
            }

            ButtonShortcut.Enabled = (_buttonShortcutEnabledCheckBox.IsChecked ?? false)
                && !string.IsNullOrWhiteSpace(ButtonShortcut.Gesture);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_volumeLabel != null)
            {
                _volumeLabel.Text = $"{(int)e.NewValue}%";
                ButtonVolume = (float)(e.NewValue / 100.0);
            }
        }
    }
}
