using System;
using SongRequestDesktopV2Rewrite.Integration;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Application entry point. Boots the Photino UI directly (no WPF).
/// </summary>
public static class Program
{
    private static PlayerSessionService? _playerSession;

    [STAThread]
    public static void Main(string[] args)
    {
        AppLaunchArgs.Parse(args);

        // AppImage mounts are read-only and Flatpak installs are immutable; move
        // downloads/library/config to the user's writable XDG data directory.
        LinuxPackaging.ConfigureAppPaths();

        // Must happen before Photino/GTK/WebKitGTK initialise: WebKitGTK reads its
        // configuration (DMA-BUF renderer, explicit sync) at load time.
        var webViewEnvironment = LinuxWebViewEnvironment.Apply();
        Print(webViewEnvironment.Describe());

        // Wayland derives the window's app id from the GLib program name, which must
        // be set before GTK starts (it is how the compositor finds the .desktop icon).
        LinuxDesktopIntegration.EnsureApplicationId();

        // A native webview failure cannot be caught, so record the attempt and only
        // clear it once the webview has actually rendered (NewUiWindow).
        StartupGuard.Begin();

        // Register the desktop implementations of the core's platform seams
        // (OpenAL, RtMidi, MPRIS/SMTC, Win32/X11 hotkeys, tray) before anything
        // touches them.
        DesktopPlatform.Register();

        AppBootstrap.Initialize();

        // Windows: keep the per-user "Apps & features" entry (and the generated
        // uninstaller) in sync with this build — including after an update.
        WindowsAppRegistration.Register();

        var host = new HeadlessHost();
        host.Music.InvokeOnUi = a => a();
        // Remembered output device (speaker button in the music player).
        host.Music.OutputDeviceNumber = ConfigService.Instance.Current?.AudioOutputDeviceNumber ?? -1;
        // Remember the queue, current track/progress, volume and previous-track
        // history across restarts. A corrupt snapshot is ignored by the service.
        _playerSession = new PlayerSessionService(host.Music);
        _ = _playerSession.RestoreAsync();
        RemoteControlService.Instance.RegisterMusicEngine(host.Music);

        var interop = new YoutubeFormInterop(host, host.YoutubeService);

        // Optional local API + MCP server (off by default, see Settings).
        AppIntegrationServer.Instance.Initialize(host, interop);

        var window = new NewUiWindow(interop, host);
        host.NewUiRef = window;

        window.RunBlocking();

        AppBootstrap.Shutdown();
    }

    private static void Print(string message)
    {
        try { Console.WriteLine("[SongRequest] " + message); } catch { }
    }

    /// <summary>
    /// Terminates the process after the app's OS-visible resources have been
    /// released. Called last by <see cref="NewUiWindow.Quit"/>.
    /// </summary>
    internal static void ExitApplication()
    {
        // Persist the latest queue/progress before the player is torn down.
        try { _playerSession?.Dispose(); } catch { }
        _playerSession = null;

        // Stops MIDI/remote control and flushes anything left.
        try { AppBootstrap.Shutdown(); } catch { }

        try { Console.Out.Flush(); } catch { }

        // Deliberately not returning from Main: Photino's native teardown can abort on
        // Linux and background native threads would otherwise keep the process alive.
        Environment.Exit(0);
    }
}
