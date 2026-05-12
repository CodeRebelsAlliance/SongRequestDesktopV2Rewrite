using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Midi;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Service for managing MIDI input and output for soundboard control
    /// </summary>
    public class MidiService : IDisposable
    {
        private MidiIn? _midiInput;
        private MidiOut? _midiOutput;
        private bool _isEnabled;
        private readonly object _midiLock = new object();

        public event EventHandler<MidiInMessageEventArgs>? MidiMessageReceived;
        public event EventHandler<string>? ErrorOccurred;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                bool disable;
                lock (_midiLock)
                {
                    _isEnabled = value;
                    disable = !_isEnabled;
                }

                if (disable)
                {
                    DisconnectDevices();
                }
            }
        }

        public int? InputDeviceNumber { get; private set; }
        public int? OutputDeviceNumber { get; private set; }

        /// <summary>
        /// Get available MIDI input devices
        /// </summary>
        public static List<MidiDeviceInfo> GetInputDevices()
        {
            var devices = new List<MidiDeviceInfo>();
            
            try
            {
                for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                {
                    try
                    {
                        var caps = MidiIn.DeviceInfo(i);
                        devices.Add(new MidiDeviceInfo
                        {
                            DeviceNumber = i,
                            Name = caps.ProductName,
                            IsInput = true
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠ Error reading MIDI input device {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error enumerating MIDI input devices: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Get available MIDI output devices
        /// </summary>
        public static List<MidiDeviceInfo> GetOutputDevices()
        {
            var devices = new List<MidiDeviceInfo>();
            
            try
            {
                for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                {
                    try
                    {
                        var caps = MidiOut.DeviceInfo(i);
                        devices.Add(new MidiDeviceInfo
                        {
                            DeviceNumber = i,
                            Name = caps.ProductName,
                            IsInput = false
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠ Error reading MIDI output device {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error enumerating MIDI output devices: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Connect to MIDI input device
        /// </summary>
        public bool ConnectInput(int deviceNumber)
        {
            try
            {
                lock (_midiLock)
                {
                    DisconnectInput_NoLock();

                    if (deviceNumber < 0 || deviceNumber >= MidiIn.NumberOfDevices)
                    {
                        ErrorOccurred?.Invoke(this, "Invalid MIDI input device number");
                        return false;
                    }

                    _midiInput = new MidiIn(deviceNumber);
                    _midiInput.MessageReceived += MidiInput_MessageReceived;
                    _midiInput.ErrorReceived += MidiInput_ErrorReceived;
                    _midiInput.Start();

                    InputDeviceNumber = deviceNumber;
                }
                System.Diagnostics.Debug.WriteLine($"✓ Connected to MIDI input device {deviceNumber}: {MidiIn.DeviceInfo(deviceNumber).ProductName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to connect MIDI input: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"✗ MIDI input connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Connect to MIDI output device
        /// </summary>
        public bool ConnectOutput(int deviceNumber)
        {
            try
            {
                lock (_midiLock)
                {
                    DisconnectOutput_NoLock();

                    if (deviceNumber < 0 || deviceNumber >= MidiOut.NumberOfDevices)
                    {
                        ErrorOccurred?.Invoke(this, "Invalid MIDI output device number");
                        return false;
                    }

                    // Retry once after a brief delay if device opens
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        try
                        {
                            _midiOutput = new MidiOut(deviceNumber);
                            OutputDeviceNumber = deviceNumber;
                            break;
                        }
                        catch (Exception retryEx) when (attempt == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"⚠ MIDI output attempt {attempt + 1} failed: {retryEx.Message}");
                            System.Threading.Thread.Sleep(100); // Brief delay before retry
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"✓ Connected to MIDI output device {deviceNumber}: {MidiOut.DeviceInfo(deviceNumber).ProductName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to connect MIDI output: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"✗ MIDI output connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnect input device
        /// </summary>
        private void DisconnectInput()
        {
            lock (_midiLock)
            {
                DisconnectInput_NoLock();
            }
        }

        private void DisconnectInput_NoLock()
        {
            if (_midiInput == null) return;

            try
            {
                _midiInput.Stop();
                _midiInput.MessageReceived -= MidiInput_MessageReceived;
                _midiInput.ErrorReceived -= MidiInput_ErrorReceived;
                _midiInput.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error disconnecting MIDI input: {ex.Message}");
            }
            finally
            {
                _midiInput = null;
                InputDeviceNumber = null;
            }
        }

        /// <summary>
        /// Disconnect output device
        /// </summary>
        private void DisconnectOutput()
        {
            lock (_midiLock)
            {
                DisconnectOutput_NoLock();
            }
        }

        private void DisconnectOutput_NoLock()
        {
            if (_midiOutput == null) return;

            try
            {
                _midiOutput.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error disconnecting MIDI output: {ex.Message}");
            }
            finally
            {
                _midiOutput = null;
                OutputDeviceNumber = null;
            }
        }

        /// <summary>
        /// Disconnect all MIDI devices
        /// </summary>
        public void DisconnectDevices()
        {
            lock (_midiLock)
            {
                DisconnectInput_NoLock();
                DisconnectOutput_NoLock();
            }
        }

        /// <summary>
        /// Send MIDI note on message (for LED feedback)
        /// </summary>
        public void SendNoteOn(int channel, int note, int velocity)
        {
            try
            {
                var msg = new NoteOnEvent(0, channel, note, velocity, 0);
                lock (_midiLock)
                {
                    if (_midiOutput == null || !_isEnabled) return;
                    _midiOutput.Send(msg.GetAsShortMessage());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error sending MIDI note on: {ex.Message}");
            }
        }

        /// <summary>
        /// Send MIDI note off message
        /// </summary>
        public void SendNoteOff(int channel, int note)
        {
            try
            {
                var msg = new NoteEvent(0, channel, MidiCommandCode.NoteOff, note, 0);
                lock (_midiLock)
                {
                    if (_midiOutput == null || !_isEnabled) return;
                    _midiOutput.Send(msg.GetAsShortMessage());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error sending MIDI note off: {ex.Message}");
            }
        }

        /// <summary>
        /// Send control change message
        /// </summary>
        public void SendControlChange(int channel, int controller, int value)
        {
            try
            {
                var msg = new ControlChangeEvent(0, channel, (MidiController)controller, value);
                lock (_midiLock)
                {
                    if (_midiOutput == null || !_isEnabled) return;
                    _midiOutput.Send(msg.GetAsShortMessage());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"⚠ Error sending MIDI control change: {ex.Message}");
            }
        }

        private void MidiInput_MessageReceived(object? sender, MidiInMessageEventArgs e)
        {
            if (!_isEnabled) return;

            // Forward the event
            MidiMessageReceived?.Invoke(this, e);

            // Debug log
            System.Diagnostics.Debug.WriteLine($"🎹 MIDI: {e.MidiEvent}");
        }

        private void MidiInput_ErrorReceived(object? sender, MidiInMessageEventArgs e)
        {
            ErrorOccurred?.Invoke(this, $"MIDI input error: {e.MidiEvent}");
            System.Diagnostics.Debug.WriteLine($"✗ MIDI input error: {e.MidiEvent}");
        }

        public void Dispose()
        {
            DisconnectDevices();
        }
    }

    /// <summary>
    /// MIDI device information
    /// </summary>
    public class MidiDeviceInfo
    {
        public int DeviceNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsInput { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? $"Device {DeviceNumber}" : Name;
        }
    }
}
