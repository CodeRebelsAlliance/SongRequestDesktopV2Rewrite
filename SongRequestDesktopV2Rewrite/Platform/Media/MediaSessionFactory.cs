using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates the platform system-media session, falling back to a no-op session on
/// platforms without a backend.
///
/// Backends: <see cref="MprisMediaSession"/> (Linux, built in) and the
/// Windows-only SMTC companion assembly (loaded at runtime by
/// <see cref="WindowsSmtcLoader"/>). macOS (MPNowPlayingInfoCenter) is not
/// implemented yet.
/// </summary>
public static class MediaSessionFactory
{
    /// <summary>Whether the current platform has a usable system-media backend.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsLinux() || WindowsSmtcLoader.Available;

    /// <summary>Name of the backend that would be used (for the settings UI).</summary>
    public static string SupportedBackendName
        => OperatingSystem.IsLinux() ? "MPRIS"
         : WindowsSmtcLoader.Available ? "SMTC"
         : "none";

    /// <summary>Connects a backend, or returns a <see cref="NullSystemMediaSession"/>.</summary>
    /// <param name="windowHandle">
    /// Top-level window handle. Required by SMTC on Windows (ignored elsewhere).
    /// </param>
    public static async System.Threading.Tasks.Task<ISystemMediaSession> CreateAsync(IntPtr windowHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var smtc = WindowsSmtcLoader.TryCreate(windowHandle);
            if (smtc is { IsSupported: true }) return smtc;
            smtc?.Dispose();
            return new NullSystemMediaSession();
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var session = await MprisMediaSession.TryCreateAsync().ConfigureAwait(false);
                if (session != null) return session;
            }
            catch
            {
                // No session bus / D-Bus unavailable: fall through to the null session.
            }
        }

        return new NullSystemMediaSession();
    }

    /// <summary>Connects a backend without a window handle (Linux/macOS path).</summary>
    public static System.Threading.Tasks.Task<ISystemMediaSession> CreateAsync() => CreateAsync(IntPtr.Zero);
}
