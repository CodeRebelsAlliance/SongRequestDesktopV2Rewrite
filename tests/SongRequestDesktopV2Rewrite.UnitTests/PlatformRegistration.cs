using System.Runtime.CompilerServices;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Registers the desktop platform backends for the unit suite, so tests observe
/// the same defaults the app has (OpenAL as the audio output, MPRIS/SMTC as the
/// system media session, RtMidi as the MIDI backend) instead of the core's
/// null fallbacks. Individual tests still override what they need.
/// </summary>
internal static class PlatformRegistration
{
    [ModuleInitializer]
    internal static void Init() => DesktopPlatform.Register();
}
