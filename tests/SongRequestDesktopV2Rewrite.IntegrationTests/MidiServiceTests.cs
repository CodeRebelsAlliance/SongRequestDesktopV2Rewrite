using System.Threading;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class MidiServiceTests
{
    // RtMidi's ALSA backend aborts the whole process when the ALSA sequencer is
    // missing, so this must be checked *before* any RtMidi object is created.
    private static void RequireMidiBackend()
        => Skip.IfNot(MidiServiceFactory.IsSupported, "no usable MIDI backend here (RtMidi/ALSA would abort the process)");

    [SkippableFact]
    public void EnumerateDevices_DoesNotThrow_AndRejectsBadIndexes()
    {
        RequireMidiBackend();

        using var svc = new RtMidiService();

        var inputs = svc.GetInputDevices();
        var outputs = svc.GetOutputDevices();

        Assert.NotNull(inputs);
        Assert.NotNull(outputs);

        Assert.False(svc.ConnectInput(-1));
        Assert.False(svc.ConnectOutput(9999));
    }

    [SkippableFact]
    public void SendWithNoDevice_IsSafeNoOp()
    {
        RequireMidiBackend();

        var svc = new RtMidiService { IsEnabled = true };
        svc.SendNoteOn(1, 1, 100);
        svc.SendNoteOff(1, 1);
        svc.SendControlChange(1, 7, 100);
        svc.Dispose();
    }

    [SkippableFact]
    public void Loopback_ViaMidiThrough_WhenAvailable()
    {
        RequireMidiBackend();

        using var svc = new RtMidiService { IsEnabled = true };

        var inputs = svc.GetInputDevices();
        var outputs = svc.GetOutputDevices();
        Skip.If(inputs.Count == 0 || outputs.Count == 0, "no MIDI devices available on this machine");

        var received = new ManualResetEventSlim(false);
        MidiMessage captured = default;
        svc.MidiMessageReceived += (_, m) => { captured = m; received.Set(); };

        Skip.IfNot(svc.ConnectInput(0), "could not open MIDI input 0");
        Skip.IfNot(svc.ConnectOutput(0), "could not open MIDI output 0");

        Thread.Sleep(150); // let the port settle
        svc.SendNoteOn(1, 60, 100);

        // Many systems expose a 'Midi Through' loopback port; if this one does not
        // echo, the test is a no-op rather than a failure.
        Skip.IfNot(received.Wait(2500), "no MIDI loopback available (no 'Midi Through' port)");

        Assert.Equal(MidiMessageType.NoteOn, captured.Type);
        Assert.Equal(1, captured.Channel);
        Assert.Equal(60, captured.Data1);
        Assert.Equal(100, captured.Data2);
    }
}
