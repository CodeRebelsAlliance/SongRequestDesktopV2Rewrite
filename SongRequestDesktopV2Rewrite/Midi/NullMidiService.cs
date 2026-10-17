using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// No-op <see cref="IMidiService"/> handed out when the platform has no usable MIDI
/// backend.
///
/// This is not just a convenience: RtMidi's ALSA backend reports failures by
/// throwing a C++ exception across the P/Invoke boundary, which **terminates the
/// process** instead of surfacing as a catchable .NET exception. On Linux without
/// the ALSA sequencer (<c>/dev/snd/seq</c>) merely enumerating devices aborts the
/// app, so <see cref="MidiServiceFactory"/> returns this implementation instead.
/// </summary>
public sealed class NullMidiService : IMidiService
{
#pragma warning disable CS0067 // Never raised: there is no MIDI backend to raise them.
    public event EventHandler<MidiMessage>? MidiMessageReceived;
    public event EventHandler<string>? ErrorOccurred;
#pragma warning restore CS0067

    public bool IsEnabled { get; set; }

    public int? InputDeviceNumber => null;

    public int? OutputDeviceNumber => null;

    public List<MidiDeviceInfo> GetInputDevices() => new();

    public List<MidiDeviceInfo> GetOutputDevices() => new();

    public bool ConnectInput(int deviceNumber) => false;

    public bool ConnectOutput(int deviceNumber) => false;

    public void DisconnectDevices() { }

    public void SendNoteOn(int channel, int note, int velocity) { }

    public void SendNoteOff(int channel, int note) { }

    public void SendControlChange(int channel, int controller, int value) { }

    public void Dispose() { }
}
