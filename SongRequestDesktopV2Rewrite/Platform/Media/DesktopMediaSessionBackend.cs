using System;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Desktop system-media backend: MPRIS on Linux (built in) and the Windows-only
/// SMTC companion assembly (loaded at runtime by <see cref="WindowsSmtcLoader"/>).
/// macOS (MPNowPlayingInfoCenter) is not implemented.
/// </summary>
internal sealed class DesktopMediaSessionBackend : ISystemMediaSessionBackend
{
    public bool IsSupported => OperatingSystem.IsLinux() || WindowsSmtcLoader.Available;

    public string Name
        => OperatingSystem.IsLinux() ? "MPRIS"
         : WindowsSmtcLoader.Available ? "SMTC"
         : "none";

    public async Task<ISystemMediaSession?> TryCreateAsync(IntPtr windowHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var smtc = WindowsSmtcLoader.TryCreate(windowHandle);
            if (smtc is { IsSupported: true }) return smtc;
            smtc?.Dispose();
            return null;
        }

        if (OperatingSystem.IsLinux())
            return await MprisMediaSession.TryCreateAsync().ConfigureAwait(false);

        return null;
    }
}
