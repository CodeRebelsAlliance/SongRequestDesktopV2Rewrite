using System.Runtime.CompilerServices;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Registers the desktop platform backends for the integration suite: these
/// tests exercise the real OpenAL output, the real RtMidi service and the real
/// MPRIS session, so they need the same registration the app performs at startup.
/// </summary>
internal static class PlatformRegistration
{
    [ModuleInitializer]
    internal static void Init() => DesktopPlatform.Register();
}
