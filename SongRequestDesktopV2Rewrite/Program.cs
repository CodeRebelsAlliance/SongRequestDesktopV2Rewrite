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
}
