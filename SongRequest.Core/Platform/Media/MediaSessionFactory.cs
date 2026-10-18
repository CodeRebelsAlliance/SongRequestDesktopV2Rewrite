using System;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// A platform "now playing" backend (MPRIS on Linux, SMTC on Windows,
/// Media3/MPNowPlayingInfoCenter on mobile).
/// </summary>
public interface ISystemMediaSessionBackend
{
    /// <summary>Whether this platform has a usable backend right now.</summary>
    bool IsSupported { get; }

    /// <summary>Backend name for the settings UI (e.g. "MPRIS", "SMTC").</summary>
    string Name { get; }

    /// <summary>Connects the backend, or returns null when it cannot connect.</summary>
    /// <param name="windowHandle">Top-level window handle; required by SMTC on Windows.</param>
    Task<ISystemMediaSession?> TryCreateAsync(IntPtr windowHandle);
}

/// <summary>
/// Creates the platform system-media session, falling back to a no-op session on
/// platforms without a backend. The host registers its backend at startup
/// (see <see cref="Register"/>); with none registered the app simply reports no
/// OS media integration, which is what the settings UI shows.
/// </summary>
public static class MediaSessionFactory
{
    private static ISystemMediaSessionBackend? _backend;

    /// <summary>Registers the platform backend (desktop: MPRIS/SMTC, mobile: Media3/NowPlaying).</summary>
    public static void Register(ISystemMediaSessionBackend? backend) => _backend = backend;

    /// <summary>Whether the current platform has a usable system-media backend.</summary>
    public static bool IsSupported => _backend is { IsSupported: true };

    /// <summary>Name of the backend that would be used (for the settings UI).</summary>
    public static string SupportedBackendName => _backend is { IsSupported: true } backend ? backend.Name : "none";

    /// <summary>Connects a backend, or returns a <see cref="NullSystemMediaSession"/>.</summary>
    /// <param name="windowHandle">
    /// Top-level window handle. Required by SMTC on Windows (ignored elsewhere).
    /// </param>
    public static async Task<ISystemMediaSession> CreateAsync(IntPtr windowHandle)
    {
        var backend = _backend;
        if (backend is { IsSupported: true })
        {
            try
            {
                var session = await backend.TryCreateAsync(windowHandle).ConfigureAwait(false);
                if (session != null) return session;
            }
            catch
            {
                // No session bus / D-Bus unavailable / no MediaSession service: fall through.
            }
        }

        return new NullSystemMediaSession();
    }

    /// <summary>Connects a backend without a window handle (Linux/macOS path).</summary>
    public static Task<ISystemMediaSession> CreateAsync() => CreateAsync(IntPtr.Zero);
}
