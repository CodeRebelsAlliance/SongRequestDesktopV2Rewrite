using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Platform-agnostic MIDI device service. The desktop host implements it with
/// RtMidi (registered through <see cref="MidiServiceFactory"/>); mobile hosts use
/// their platform MIDI API.
/// (cross-platform).
/// </summary>
public interface IMidiService : IDisposable
{
    event EventHandler<MidiMessage>? MidiMessageReceived;
    event EventHandler<string>? ErrorOccurred;

    bool IsEnabled { get; set; }

    int? InputDeviceNumber { get; }
    int? OutputDeviceNumber { get; }

    List<MidiDeviceInfo> GetInputDevices();
    List<MidiDeviceInfo> GetOutputDevices();

    bool ConnectInput(int deviceNumber);
    bool ConnectOutput(int deviceNumber);
    void DisconnectDevices();

    void SendNoteOn(int channel, int note, int velocity);
    void SendNoteOff(int channel, int note);
    void SendControlChange(int channel, int controller, int value);
}
