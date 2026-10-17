using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Application entry point. Boots the Photino UI directly (no WPF).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLaunchArgs.Parse(args);

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

        AppBootstrap.Initialize();

        // Windows: keep the per-user "Apps & features" entry (and the generated
        // uninstaller) in sync with this build — including after an update.
        WindowsAppRegistration.Register();

        var host = new HeadlessHost();
        host.Music.InvokeOnUi = a => a();
        RemoteControlService.Instance.RegisterMusicEngine(host.Music);

        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var window = new NewUiWindow(interop, host);
        host.NewUiRef = window;

        window.RunBlocking();

        AppBootstrap.Shutdown();
    }

    private static void Print(string message)
    {
        try { Console.WriteLine("[SongRequest] " + message); } catch { }
    }
}
