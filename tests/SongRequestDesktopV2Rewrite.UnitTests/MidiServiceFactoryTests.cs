using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Guards the fix for a native crash: RtMidi's ALSA backend throws a C++ exception
/// through the P/Invoke boundary when the ALSA sequencer is missing, which kills the
/// process instead of raising a catchable error. MIDI must therefore be reported as
/// unsupported — and never touched — on such machines.
/// </summary>
public class MidiServiceFactoryTests
{
    [Fact]
    public void IsPlatformSupported_OnlyWhereMidiCanActuallyWork()
    {
        Assert.True(MidiServiceFactory.IsPlatformSupported(isWindows: true, isMacOS: false, isLinux: false, alsaSequencerAvailable: false));
        Assert.True(MidiServiceFactory.IsPlatformSupported(isWindows: false, isMacOS: true, isLinux: false, alsaSequencerAvailable: false));
        Assert.True(MidiServiceFactory.IsPlatformSupported(isWindows: false, isMacOS: false, isLinux: true, alsaSequencerAvailable: true));

        // Linux without /dev/snd/seq: this is the case that crashed the test host.
        Assert.False(MidiServiceFactory.IsPlatformSupported(isWindows: false, isMacOS: false, isLinux: true, alsaSequencerAvailable: false));
        Assert.False(MidiServiceFactory.IsPlatformSupported(isWindows: false, isMacOS: false, isLinux: false, alsaSequencerAvailable: false));
    }

    [Fact]
    public void Create_NeverReturnsNull()
    {
        var saved = MidiServiceFactory.Override;
        try
        {
            MidiServiceFactory.Override = null;
            using var service = MidiServiceFactory.Create();
            Assert.NotNull(service);
        }
        finally
        {
            MidiServiceFactory.Override = saved;
        }
    }

    [Fact]
    public void Override_IsUsedWhenSet()
    {
        var saved = MidiServiceFactory.Override;
        try
        {
            var fake = new FakeMidiService();
            MidiServiceFactory.Override = () => fake;

            Assert.Same(fake, MidiServiceFactory.Create());
        }
        finally
        {
            MidiServiceFactory.Override = saved;
        }
    }

    [Fact]
    public void UnitTests_NeverTouchNativeMidi()
        => Assert.IsType<FakeMidiService>(MidiServiceFactory.Create());

    [Fact]
    public void NullMidiService_IsASafeNoOp()
    {
        using var service = new NullMidiService();

        Assert.Empty(service.GetInputDevices());
        Assert.Empty(service.GetOutputDevices());
        Assert.False(service.ConnectInput(0));
        Assert.False(service.ConnectOutput(0));
        Assert.Null(service.InputDeviceNumber);
        Assert.Null(service.OutputDeviceNumber);

        service.IsEnabled = true;
        service.SendNoteOn(1, 60, 100);
        service.SendNoteOff(1, 60);
        service.SendControlChange(1, 7, 100);
        service.DisconnectDevices();
    }
}
