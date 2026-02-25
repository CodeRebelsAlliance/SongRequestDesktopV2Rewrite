using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SongRequestDesktopV2Rewrite
{
    public partial class MidiSettingsDialog : Window
    {
        private readonly MidiService _midiService;
        private readonly SoundboardConfiguration _config;

        public MidiSettingsDialog(MidiService midiService, SoundboardConfiguration config)
        {
            InitializeComponent();
            _midiService = midiService;
            _config = config;

            LoadDevices();
            UpdateStatus();

            // Set empty button velocity from config
            var velocity = ParseVelocityFromColor(_config.EmptyButtonFeedbackColor);
            EmptyButtonVelocitySlider.Value = velocity;
        }

        private void LoadDevices()
        {
            // Load input devices
            MidiInputCombo.Items.Clear();
            MidiInputCombo.Items.Add(new MidiDeviceInfo { DeviceNumber = -1, Name = "None", IsInput = true });

            var inputDevices = MidiService.GetInputDevices();
            foreach (var device in inputDevices)
            {
                MidiInputCombo.Items.Add(device);
            }

            // Select current device
            var selectedInput = MidiInputCombo.Items.Cast<MidiDeviceInfo>()
                .FirstOrDefault(d => d.DeviceNumber == _config.MidiInputDevice);
            MidiInputCombo.SelectedItem = selectedInput ?? MidiInputCombo.Items[0];

            // Load output devices
            MidiOutputCombo.Items.Clear();
            MidiOutputCombo.Items.Add(new MidiDeviceInfo { DeviceNumber = -1, Name = "None", IsInput = false });

            var outputDevices = MidiService.GetOutputDevices();
            foreach (var device in outputDevices)
            {
                MidiOutputCombo.Items.Add(device);
            }

            // Select current device
            var selectedOutput = MidiOutputCombo.Items.Cast<MidiDeviceInfo>()
                .FirstOrDefault(d => d.DeviceNumber == _config.MidiOutputDevice);
            MidiOutputCombo.SelectedItem = selectedOutput ?? MidiOutputCombo.Items[0];

            System.Diagnostics.Debug.WriteLine($"✓ Loaded {inputDevices.Count} MIDI input devices, {outputDevices.Count} output devices");
        }

        private void MidiInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MidiInputCombo.SelectedItem is MidiDeviceInfo device)
            {
                _config.MidiInputDevice = device.DeviceNumber;

                if (_midiService.IsEnabled)
                {
                    if (device.DeviceNumber >= 0)
                    {
                        _midiService.ConnectInput(device.DeviceNumber);
                    }
                    else
                    {
                        _midiService.DisconnectDevices();
                        _midiService.IsEnabled = false;
                    }
                }

                _config.Save();
                UpdateStatus();
            }
        }

        private void MidiOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MidiOutputCombo.SelectedItem is MidiDeviceInfo device)
            {
                _config.MidiOutputDevice = device.DeviceNumber;

                if (_midiService.IsEnabled)
                {
                    if (device.DeviceNumber >= 0)
                    {
                        _midiService.ConnectOutput(device.DeviceNumber);
                    }
                }

                _config.Save();
                UpdateStatus();
            }
        }

        private void EmptyButtonVelocitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (EmptyButtonVelocityLabel != null)
            {
                int velocity = (int)e.NewValue;
                EmptyButtonVelocityLabel.Text = velocity.ToString();

                // Store as hex color representation for consistency
                _config.EmptyButtonFeedbackColor = VelocityToColor(velocity);
                _config.Save();
            }
        }

        private void TestFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_midiService.IsEnabled || _midiService.OutputDeviceNumber == null)
            {
                MessageBox.Show("Please enable MIDI and select an output device first.", 
                    "MIDI Test", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Send test pattern: light up all notes on channel 1 briefly
            for (int note = 0; note < 128; note++)
            {
                _midiService.SendNoteOn(1, note, 64); // Medium brightness
            }

            // Turn off after 500ms
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (s, ev) =>
            {
                for (int note = 0; note < 128; note++)
                {
                    _midiService.SendNoteOff(1, note);
                }
                timer.Stop();
            };
            timer.Start();

            StatusText.Text = "Test pattern sent - all LEDs should flash briefly";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void UpdateStatus()
        {
            bool hasInput = _config.MidiInputDevice >= 0;
            bool hasOutput = _config.MidiOutputDevice >= 0;

            if (hasInput && hasOutput)
            {
                StatusText.Text = $"✓ Connected: Input & Output ready";
            }
            else if (hasInput)
            {
                StatusText.Text = $"⚠ Input connected, but no output device (no LED feedback)";
            }
            else if (hasOutput)
            {
                StatusText.Text = $"⚠ Output connected, but no input device (no controller input)";
            }
            else
            {
                StatusText.Text = "No MIDI devices connected";
            }
        }

        private string VelocityToColor(int velocity)
        {
            // Store velocity as brightness in hex format (for future use)
            int brightness = (velocity * 255) / 127;
            return $"#{brightness:X2}{brightness:X2}{brightness:X2}";
        }

        private int ParseVelocityFromColor(string color)
        {
            try
            {
                if (string.IsNullOrEmpty(color) || !color.StartsWith("#"))
                    return 10; // Default

                // Parse hex color (assuming grayscale for velocity)
                var hex = color.Substring(1);
                if (hex.Length >= 2)
                {
                    int brightness = Convert.ToInt32(hex.Substring(0, 2), 16);
                    return (brightness * 127) / 255;
                }
            }
            catch
            {
                // Fallback
            }

            return 10; // Default
        }
    }
}
