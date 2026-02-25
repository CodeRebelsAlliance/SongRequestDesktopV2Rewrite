using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Midi;

namespace SongRequestDesktopV2Rewrite
{
    public partial class MidiAssignDialog : Window
    {
        private readonly MidiService _midiService;
        private MidiMapping? _result;
        private bool _isWaitingForInput = true;
        private DispatcherTimer? _timeoutTimer;
        private int _detectedChannel;
        private int _detectedNote;
        private string _detectedMessageType = "NoteOn";

        public MidiMapping? Result => _result;

        public MidiAssignDialog(MidiService midiService, MidiMapping? existingMapping = null)
        {
            InitializeComponent();
            _midiService = midiService;

            // Subscribe to MIDI events
            _midiService.MidiMessageReceived += MidiService_MidiMessageReceived;

            // Pre-fill existing values if editing
            if (existingMapping != null && existingMapping.IsConfigured)
            {
                VelocityPressedSlider.Value = existingMapping.VelocityPressed;
                VelocityUnpressedSlider.Value = existingMapping.VelocityUnpressed;
                StatusText.Text = $"Current: Channel {existingMapping.Channel}, Note {existingMapping.Note}\n\nPress a button on your MIDI controller...";
            }
            else
            {
                StatusText.Text = "Waiting for MIDI input...\n\nPress a button on your MIDI controller to assign it to this soundboard button.";
            }

            // Start timeout timer (15 seconds)
            _timeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _timeoutTimer.Tick += TimeoutTimer_Tick;
            _timeoutTimer.Start();
        }

        private void MidiService_MidiMessageReceived(object? sender, MidiInMessageEventArgs e)
        {
            if (!_isWaitingForInput) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Parse MIDI message
                    if (e.MidiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0)
                    {
                        _detectedChannel = noteOn.Channel;
                        _detectedNote = noteOn.NoteNumber;
                        _detectedMessageType = "NoteOn";

                        StatusText.Text = $"✓ Detected MIDI Note On\n\nChannel: {_detectedChannel}\nNote: {_detectedNote}\n\nAdjust feedback LED colors below and click OK to confirm.";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        
                        _isWaitingForInput = false;
                        _timeoutTimer?.Stop();
                        
                        OkButton.IsEnabled = true;
                        VelocityPressedSlider.IsEnabled = true;
                        VelocityUnpressedSlider.IsEnabled = true;

                        System.Diagnostics.Debug.WriteLine($"🎹 Detected MIDI: Channel {_detectedChannel}, Note {_detectedNote}");
                    }
                    else if (e.MidiEvent is ControlChangeEvent cc)
                    {
                        _detectedChannel = cc.Channel;
                        _detectedNote = (int)cc.Controller;
                        _detectedMessageType = "ControlChange";
                        
                        StatusText.Text = $"✓ Detected MIDI Control Change\n\nChannel: {_detectedChannel}\nController: {_detectedNote}\n\nAdjust feedback values below and click OK to confirm.";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        
                        _isWaitingForInput = false;
                        _timeoutTimer?.Stop();
                        
                        OkButton.IsEnabled = true;
                        VelocityPressedSlider.IsEnabled = true;
                        VelocityUnpressedSlider.IsEnabled = true;

                        System.Diagnostics.Debug.WriteLine($"🎹 Detected MIDI CC: Channel {_detectedChannel}, Controller {_detectedNote}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error processing MIDI input: {ex.Message}");
                }
            });
        }

        private void TimeoutTimer_Tick(object? sender, EventArgs e)
        {
            _timeoutTimer?.Stop();
            StatusText.Text = "Timeout - No MIDI input detected";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // Red
            CancelButton.Content = "Close";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            _result = new MidiMapping
            {
                Channel = _detectedChannel,
                Note = _detectedNote,
                MessageType = _detectedMessageType,
                VelocityPressed = (int)VelocityPressedSlider.Value,
                VelocityUnpressed = (int)VelocityUnpressedSlider.Value
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void VelocityPressedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VelocityPressedLabel != null)
            {
                VelocityPressedLabel.Text = ((int)e.NewValue).ToString();
            }
        }

        private void VelocityUnpressedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VelocityUnpressedLabel != null)
            {
                VelocityUnpressedLabel.Text = ((int)e.NewValue).ToString();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _midiService.MidiMessageReceived -= MidiService_MidiMessageReceived;
            _timeoutTimer?.Stop();
        }
    }
}
