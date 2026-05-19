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
                    var me = e.MidiEvent;
                    if (me == null) return;

                    int channel = 0;
                    if (me is NoteOnEvent noev) channel = noev.Channel;
                    else if (me is NoteEvent nev) channel = nev.Channel;
                    else if (me is ControlChangeEvent ccev) channel = ccev.Channel;
                    else if (me is PitchWheelChangeEvent pwev) channel = pwev.Channel;
                    else
                    {
                        // Fallback: derive channel from raw status byte (lower 4 bits)
                        int rawStatus = e.RawMessage & 0xFF;
                        channel = (rawStatus & 0x0F);
                    }

                    int value1 = 0;
                    int value2 = 0;
                    string msgType = me.CommandCode.ToString();

                    if (me is NoteOnEvent noteOn && noteOn.Velocity > 0)
                    {
                        value1 = noteOn.NoteNumber;
                        value2 = noteOn.Velocity;
                        msgType = "NoteOn";
                    }
                    else if (me is ControlChangeEvent cc)
                    {
                        value1 = (int)cc.Controller;
                        value2 = cc.ControllerValue;
                        msgType = "ControlChange";
                    }
                    else if (me is NoteEvent ne)
                    {
                        value1 = ne.NoteNumber;
                        value2 = (ne is NoteOnEvent nOn) ? nOn.Velocity : 0;
                        msgType = ne.CommandCode.ToString();
                    }
                    else if (me is PitchWheelChangeEvent pw)
                    {
                        value1 = pw.Pitch;
                        value2 = 0;
                        msgType = "PitchWheel";
                    }
                    else
                    {
                        // Fallback: parse raw message bytes so unusual devices are still detected (e.g., relative encoders)
                        int raw = e.RawMessage;
                        value1 = (raw >> 8) & 0xFF;
                        value2 = (raw >> 16) & 0xFF;
                        msgType = me.CommandCode.ToString();
                    }

                    _detectedChannel = channel;
                    _detectedNote = value1;
                    _detectedMessageType = msgType;

                    StatusText.Text = $"✓ Detected MIDI {msgType}\n\nChannel: {_detectedChannel}\nValue1: {_detectedNote}\nValue2: {value2}\n\nAdjust feedback values below and click OK to confirm.";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green

                    _isWaitingForInput = false;
                    _timeoutTimer?.Stop();

                    OkButton.IsEnabled = true;
                    VelocityPressedSlider.IsEnabled = true;
                    VelocityUnpressedSlider.IsEnabled = true;

                    System.Diagnostics.Debug.WriteLine($"🎹 Detected MIDI {msgType}: Channel {_detectedChannel}, Value1 {_detectedNote}, Value2 {value2}");
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
