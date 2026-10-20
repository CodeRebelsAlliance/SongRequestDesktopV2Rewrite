namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// WPF-free application bootstrap.
///
/// Initializes app-level services that have no UI dependency, so they can be
/// shared by the current WPF entry point and the future Photino-only entry
/// point. Keep this class free of <c>System.Windows</c> so it can move to a
/// cross-platform target unchanged.
/// </summary>
public static class AppBootstrap
{
    private static readonly object _sync = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (_sync)
        {
            if (_initialized) return;
            _initialized = true;
        }

        // Load configuration early (also creates the config file on first run).
        _ = ConfigService.Instance.Current;

        // Start remote control (MIDI / keyboard / remote mappings).
        RemoteControlService.Instance.Initialize();
    }

    public static void Shutdown()
    {
        try { Integration.AppIntegrationServer.Instance.Stop(); } catch { }
        try { RemoteControlService.Instance.Dispose(); } catch { }
        lock (_sync) { _initialized = false; }
    }
}
