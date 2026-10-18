using System;
using System.Collections.Generic;
using Android.Content;
using Android.Media.Midi;
using SongRequestDesktopV2Rewrite;
using CoreMidiDeviceInfo = SongRequestDesktopV2Rewrite.MidiDeviceInfo;

namespace SongRequest.Mobile;

/// <summary>
/// Android MIDI backend (<see cref="IMidiService"/>) over <c>MidiManager</c>:
/// USB controllers and Bluetooth LE MIDI both show up here, hot-plug included.
///
/// The device list is the one <c>MidiManager</c> reports, numbered by index — the
/// same numbering the settings/remote-control UI stores, so the mapping and
/// feedback code in the core is shared with the desktop unchanged.
/// </summary>
internal sealed class AndroidMidiService : IMidiService
{
    private readonly MidiManager? _manager;
    private readonly object _sync = new();
    private readonly DeviceCallback _deviceCallback;
    private readonly Receiver _receiver;

    private Android.Media.Midi.MidiDeviceInfo[] _devices = Array.Empty<Android.Media.Midi.MidiDeviceInfo>();

    private Android.Media.Midi.MidiDeviceInfo? _receiveInfo;
    private Android.Media.Midi.MidiDevice? _receiveDevice;
    private MidiOutputPort? _receivePort;

    private Android.Media.Midi.MidiDeviceInfo? _sendInfo;
    private Android.Media.Midi.MidiDevice? _sendDevice;
    private MidiInputPort? _sendPort;

    private int _lastStatus;
    private bool _enabled = true;
    private bool _disposed;

    public AndroidMidiService()
    {
        _deviceCallback = new DeviceCallback(this);
        _receiver = new Receiver(this);

        try
        {
            _manager = (MidiManager?)Android.App.Application.Context.GetSystemService(Context.MidiService);
            if (_manager != null)
            {
                RefreshDevices();
                // RegisterDeviceCallback(callback, handler) is deprecated on API 33+
                // in favour of the transport-filtered overload; it still works on
                // every supported version, and the filtered variant's flag enum is
                // not exposed by the bindings we target.
#pragma warning disable CA1422
                _manager.RegisterDeviceCallback(_deviceCallback, null);
#pragma warning restore CA1422
            }
        }
        catch (Exception ex)
        {
            Log($"MIDI unavailable: {ex.Message}");
        }
    }

    public event EventHandler<MidiMessage>? MidiMessageReceived;

    public event EventHandler<string>? ErrorOccurred;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) DisconnectDevices();
        }
    }

    public int? InputDeviceNumber { get; private set; }

    public int? OutputDeviceNumber { get; private set; }

    public List<CoreMidiDeviceInfo> GetInputDevices() => Enumerate(isInput: true);

    public List<CoreMidiDeviceInfo> GetOutputDevices() => Enumerate(isInput: false);

    private List<CoreMidiDeviceInfo> Enumerate(bool isInput)
    {
        var result = new List<CoreMidiDeviceInfo>();

        try
        {
            RefreshDevices();
            for (int i = 0; i < _devices.Length; i++)
            {
                var device = _devices[i];
                var ports = isInput ? device.OutputPortCount : device.InputPortCount;
                if (ports <= 0) continue;

                result.Add(new CoreMidiDeviceInfo
                {
                    DeviceNumber = i,
                    Name = Describe(device),
                    IsInput = isInput,
                });
            }
        }
        catch (Exception ex)
        {
            Log($"MIDI device enumeration failed: {ex.Message}");
        }

        return result;
    }

    public bool ConnectInput(int deviceNumber)
    {
        lock (_sync)
        {
            try
            {
                DisconnectInput();
                if (!_enabled || _manager == null) return false;
                if (deviceNumber < 0 || deviceNumber >= _devices.Length) return false;

                var device = _devices[deviceNumber];
                _receiveInfo = device;
                InputDeviceNumber = deviceNumber;

                // Opening is asynchronous; the port is connected in OnDeviceOpened.
                _manager.OpenDevice(device, new DeviceOpenedListener(this), null);
                Log($"MIDI input opening: {Describe(device)}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"MIDI input failed: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }
    }

    public bool ConnectOutput(int deviceNumber)
    {
        lock (_sync)
        {
            try
            {
                DisconnectOutput();
                if (!_enabled || _manager == null) return false;
                if (deviceNumber < 0 || deviceNumber >= _devices.Length) return false;

                var device = _devices[deviceNumber];
                _sendInfo = device;
                OutputDeviceNumber = deviceNumber;

                _manager.OpenDevice(device, new DeviceOpenedListener(this), null);
                Log($"MIDI output opening: {Describe(device)}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"MIDI output failed: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }
    }

    public void DisconnectDevices()
    {
        lock (_sync)
        {
            DisconnectInput();
            DisconnectOutput();
        }
    }

    private void DisconnectInput()
    {
        try { _receivePort?.Close(); } catch { }
        _receivePort = null;

        try { _receiveDevice?.Close(); } catch { }
        _receiveDevice = null;
        _receiveInfo = null;
        InputDeviceNumber = null;
    }

    private void DisconnectOutput()
    {
        try { _sendPort?.Close(); } catch { }
        _sendPort = null;

        try { _sendDevice?.Close(); } catch { }
        _sendDevice = null;
        _sendInfo = null;
        OutputDeviceNumber = null;
    }

    public void SendNoteOn(int channel, int note, int velocity)
        => Send(new byte[] { (byte)(0x90 | (channel & 0x0F)), (byte)(note & 0x7F), (byte)(velocity & 0x7F) });

    public void SendNoteOff(int channel, int note)
        => Send(new byte[] { (byte)(0x80 | (channel & 0x0F)), (byte)(note & 0x7F), 0 });

    public void SendControlChange(int channel, int controller, int value)
        => Send(new byte[] { (byte)(0xB0 | (channel & 0x0F)), (byte)(controller & 0x7F), (byte)(value & 0x7F) });

    private void Send(byte[] message)
    {
        try
        {
            var port = _sendPort;
            if (port == null) return;
            port.Send(message, 0, message.Length);
        }
        catch (Exception ex)
        {
            Log($"MIDI send failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _manager?.UnregisterDeviceCallback(_deviceCallback); } catch { }
        DisconnectDevices();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private void RefreshDevices()
    {
        if (_manager == null) return;

        // GetDevices() is deprecated on API 33+ but works everywhere we support;
        // the transport-filtered replacement needs the transport flags, which the
        // bindings we target do not expose.
#pragma warning disable CA1422
        _devices = _manager.GetDevices() ?? Array.Empty<Android.Media.Midi.MidiDeviceInfo>();
#pragma warning restore CA1422
    }

    internal void OnDeviceOpened(Android.Media.Midi.MidiDevice device)
    {
        lock (_sync)
        {
            try
            {
                var info = device.Info;
                if (info == null) return;

                if (ReferenceEquals(info, _receiveInfo) && _receivePort == null)
                {
                    if (info.OutputPortCount <= 0) return;
                    _receiveDevice = device;
                    _receivePort = device.OpenOutputPort(0);
                    _receivePort?.Connect(_receiver);
                    Log($"MIDI input connected: {Describe(info)}");
                }

                if (ReferenceEquals(info, _sendInfo) && _sendPort == null)
                {
                    if (info.InputPortCount <= 0) return;
                    _sendDevice = device;
                    _sendPort = device.OpenInputPort(0);
                    Log($"MIDI output connected: {Describe(info)}");
                }
            }
            catch (Exception ex)
            {
                Log($"MIDI open failed: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
            }
        }
    }

    internal void OnMessage(byte[] data, int offset, int count)
    {
        if (!_enabled || _disposed) return;

        try
        {
            int lastStatus = _lastStatus;
            var message = MidiBytesParser.Parse(data, offset, count, ref lastStatus);
            _lastStatus = lastStatus;

            if (message is { } parsed)
                MidiMessageReceived?.Invoke(this, parsed);
        }
        catch (Exception ex)
        {
            Log($"MIDI parse failed: {ex.Message}");
        }
    }

    internal void OnDeviceAdded()
    {
        RefreshDevices();
        Log($"MIDI device attached ({_devices.Length} now)");
    }

    internal void OnDeviceRemoved()
    {
        RefreshDevices();
        Log($"MIDI device detached ({_devices.Length} now)");
    }

    private static string Describe(Android.Media.Midi.MidiDeviceInfo device)
    {
        try
        {
            var properties = device.Properties;
            var name = properties?.GetString(Android.Media.Midi.MidiDeviceInfo.PropertyName);
            if (!string.IsNullOrWhiteSpace(name)) return name!;
        }
        catch
        {
            // Fall through to the type-based description.
        }

        var transport = device.Type == MidiDeviceType.Bluetooth ? "Bluetooth" : "USB";
        return $"MIDI device ({transport})";
    }

    private static void Log(string message)
        => Android.Util.Log.Debug("SongRequest", message);

    /// <summary>Receives raw MIDI from the device's output port.</summary>
    private sealed class Receiver : MidiReceiver
    {
        private readonly AndroidMidiService _owner;

        public Receiver(AndroidMidiService owner) => _owner = owner;

        public override void OnSend(byte[]? msg, int offset, int count, long timestamp)
        {
            if (msg == null) return;
            _owner.OnMessage(msg, offset, count);
        }
    }

    private sealed class DeviceOpenedListener : Java.Lang.Object, MidiManager.IOnDeviceOpenedListener
    {
        private readonly AndroidMidiService _owner;

        public DeviceOpenedListener(AndroidMidiService owner) => _owner = owner;

        public void OnDeviceOpened(Android.Media.Midi.MidiDevice? device)
        {
            if (device != null) _owner.OnDeviceOpened(device);
        }
    }

    private sealed class DeviceCallback : MidiManager.DeviceCallback
    {
        private readonly AndroidMidiService _owner;

        public DeviceCallback(AndroidMidiService owner) => _owner = owner;

        public override void OnDeviceAdded(Android.Media.Midi.MidiDeviceInfo? device) => _owner.OnDeviceAdded();

        public override void OnDeviceRemoved(Android.Media.Midi.MidiDeviceInfo? device) => _owner.OnDeviceRemoved();
    }
}
