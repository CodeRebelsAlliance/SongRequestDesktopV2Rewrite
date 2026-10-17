using System;
using System.Collections.Generic;
using System.Linq;
using RtMidi.Core;
using RtMidi.Core.Devices;
using RtMidi.Core.Devices.Infos;
using RtMidi.Core.Enums;
using RtMidi.Core.Messages;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform <see cref="IMidiService"/> backed by RtMidi (WinMM / ALSA /
/// CoreMIDI). Replaces the Windows-only NAudio MIDI service on non-Windows
/// platforms. Messages are normalized to <see cref="MidiMessage"/> so the
/// existing mapping / feedback logic is unchanged.
/// </summary>
internal sealed class RtMidiService : IMidiService
{
    private readonly object _lock = new();

    private List<IMidiInputDeviceInfo> _inputInfos = new();
    private List<IMidiOutputDeviceInfo> _outputInfos = new();
    private IMidiInputDevice? _input;
    private IMidiOutputDevice? _output;
    private bool _isEnabled;

    public event EventHandler<MidiMessage>? MidiMessageReceived;
    public event EventHandler<string>? ErrorOccurred;

    public int? InputDeviceNumber { get; private set; }
    public int? OutputDeviceNumber { get; private set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!value) DisconnectDevices();
        }
    }

    public List<MidiDeviceInfo> GetInputDevices()
    {
        lock (_lock)
        {
            RefreshDevices();
            return _inputInfos
                .Select((d, i) => new MidiDeviceInfo { DeviceNumber = i, Name = d.Name, IsInput = true })
                .ToList();
        }
    }

    public List<MidiDeviceInfo> GetOutputDevices()
    {
        lock (_lock)
        {
            RefreshDevices();
            return _outputInfos
                .Select((d, i) => new MidiDeviceInfo { DeviceNumber = i, Name = d.Name, IsInput = false })
                .ToList();
        }
    }

    public bool ConnectInput(int deviceNumber)
    {
        lock (_lock)
        {
            DisconnectInputNoLock();
            RefreshDevices();

            if (deviceNumber < 0 || deviceNumber >= _inputInfos.Count)
            {
                RaiseError("Invalid MIDI input device number");
                return false;
            }

            try
            {
                var device = _inputInfos[deviceNumber].CreateDevice();
                device.NoteOn += OnNoteOn;
                device.NoteOff += OnNoteOff;
                device.ControlChange += OnControlChange;
                device.PitchBend += OnPitchBend;

                if (!device.Open())
                {
                    RaiseError($"Failed to open MIDI input '{device.Name}'");
                    return false;
                }

                _input = device;
                InputDeviceNumber = deviceNumber;
                return true;
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to connect MIDI input: {ex.Message}");
                return false;
            }
        }
    }

    public bool ConnectOutput(int deviceNumber)
    {
        lock (_lock)
        {
            DisconnectOutputNoLock();
            RefreshDevices();

            if (deviceNumber < 0 || deviceNumber >= _outputInfos.Count)
            {
                RaiseError("Invalid MIDI output device number");
                return false;
            }

            try
            {
                var device = _outputInfos[deviceNumber].CreateDevice();
                if (!device.Open())
                {
                    RaiseError($"Failed to open MIDI output '{device.Name}'");
                    return false;
                }

                _output = device;
                OutputDeviceNumber = deviceNumber;
                return true;
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to connect MIDI output: {ex.Message}");
                return false;
            }
        }
    }

    public void DisconnectDevices()
    {
        lock (_lock)
        {
            DisconnectInputNoLock();
            DisconnectOutputNoLock();
        }
    }

    private void DisconnectInputNoLock()
    {
        var device = _input;
        _input = null;
        InputDeviceNumber = null;
        if (device == null) return;

        try
        {
            device.NoteOn -= OnNoteOn;
            device.NoteOff -= OnNoteOff;
            device.ControlChange -= OnControlChange;
            device.PitchBend -= OnPitchBend;
            device.Close();
        }
        catch { }
    }

    private void DisconnectOutputNoLock()
    {
        var device = _output;
        _output = null;
        OutputDeviceNumber = null;
        try { device?.Close(); } catch { }
    }

    private void RefreshDevices()
    {
        try
        {
            _inputInfos = MidiDeviceManager.Default.InputDevices.ToList();
            _outputInfos = MidiDeviceManager.Default.OutputDevices.ToList();
        }
        catch (Exception ex)
        {
            _inputInfos = new List<IMidiInputDeviceInfo>();
            _outputInfos = new List<IMidiOutputDeviceInfo>();
            RaiseError($"MIDI enumeration failed: {ex.Message}");
        }
    }

    public void SendNoteOn(int channel, int note, int velocity)
    {
        lock (_lock)
        {
            if (_output == null || !_isEnabled) return;
            try { _output.Send(new NoteOnMessage(ToChannel(channel), ToKey(note), velocity)); } catch { }
        }
    }

    public void SendNoteOff(int channel, int note)
    {
        lock (_lock)
        {
            if (_output == null || !_isEnabled) return;
            try { _output.Send(new NoteOffMessage(ToChannel(channel), ToKey(note), 0)); } catch { }
        }
    }

    public void SendControlChange(int channel, int controller, int value)
    {
        lock (_lock)
        {
            if (_output == null || !_isEnabled) return;
            try { _output.Send(new ControlChangeMessage(ToChannel(channel), controller, value)); } catch { }
        }
    }

    private void OnNoteOn(IMidiInputDevice sender, in NoteOnMessage msg)
        => Emit(new MidiMessage(MidiMessageType.NoteOn, FromChannel(msg.Channel), (int)msg.Key, msg.Velocity, "NoteOn"));

    private void OnNoteOff(IMidiInputDevice sender, in NoteOffMessage msg)
        => Emit(new MidiMessage(MidiMessageType.NoteOff, FromChannel(msg.Channel), (int)msg.Key, msg.Velocity, "NoteOff"));

    private void OnControlChange(IMidiInputDevice sender, in ControlChangeMessage msg)
        => Emit(new MidiMessage(MidiMessageType.ControlChange, FromChannel(msg.Channel), msg.Control, msg.Value, "ControlChange"));

    private void OnPitchBend(IMidiInputDevice sender, in PitchBendMessage msg)
        => Emit(new MidiMessage(MidiMessageType.PitchWheel, FromChannel(msg.Channel), msg.Value, 0, "PitchWheel"));

    private void Emit(MidiMessage message)
    {
        if (!_isEnabled) return;
        MidiMessageReceived?.Invoke(this, message);
    }

    // NAudio/our convention: channels are 1-16. RtMidi uses a 0-15 enum.
    private static Channel ToChannel(int channel) => (Channel)Math.Clamp(channel - 1, 0, 15);

    private static Key ToKey(int note) => (Key)Math.Clamp(note, 0, 127);

    private static int FromChannel(Channel channel) => (int)channel + 1;

    private void RaiseError(string message) => ErrorOccurred?.Invoke(this, message);

    public void Dispose() => DisconnectDevices();
}
