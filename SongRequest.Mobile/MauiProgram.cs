using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Mobile entry point: points the shared core at the app sandbox, wires the
/// platform seams, and builds the MAUI shell that hosts the web UI.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // The app bundle is read-only on both platforms: every writable folder
        // (downloads, caches, library, config) moves under the sandbox root.
        AppPaths.UseDataRoot(FileSystem.AppDataDirectory);

        // Register the platform seams. M1 has no audio backend yet, so the core's
        // silent NullAudioOutput is used; M2 adds AudioTrack/AVAudioEngine here.
        MobilePlatform.Register();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Font Awesome solid: the Material-style navigation-bar icons (the web UI
        // uses the same icon set, so the native bar matches it).
        builder.ConfigureFonts(fonts => fonts.AddFont("FaSolid.ttf", "FaSolid"));

#if DEBUG
        // Chrome DevTools / Safari Web Inspector for the embedded web UI.
        builder.Services.AddHybridWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<AppHost>();

        return builder.Build();
    }
}

/// <summary>The MAUI application object; the shell lives in <see cref="MainPage"/>.</summary>
public sealed class App : Application
{
    private readonly MainPage _mainPage;

    // AppHost is injected so the interop/host wiring happens before the WebView loads.
    public App(MainPage mainPage, AppHost host)
    {
        _mainPage = mainPage;
        _ = host;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_mainPage);
}
