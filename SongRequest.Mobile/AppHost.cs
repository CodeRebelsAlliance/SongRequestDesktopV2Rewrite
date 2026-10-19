using System;
using System.Collections.Generic;
using Microsoft.Maui.ApplicationModel;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Wires the shared core to the mobile shell: creates the host + interop (the
/// same objects the desktop entry point creates), routes page messages and their
/// responses back to the right web view, and marshals engine callbacks onto the
/// UI thread.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly MainPage _page;

    /// <summary>The running host (single instance; the stage helpers reach the engine through it).</summary>
    public static AppHost? Current { get; private set; }

    public HeadlessHost Host { get; }
    public YoutubeFormInterop Interop { get; }

    /// <summary>The shell page (used by the activity to react to memory pressure).</summary>
    public MainPage Page => _page;
    public MobileShell Shell { get; }

    public AppHost(MainPage page)
    {
        _page = page;
        Current = this;
        Shell = new MobileShell(page);

        Host = new HeadlessHost { NewUiRef = Shell };
        Host.Music.InvokeOnUi = RunOnUi;
        Host.Music.OutputDeviceNumber = ConfigService.Instance.Current?.AudioOutputDeviceNumber ?? -1;

        Interop = new YoutubeFormInterop(Host, Host.YoutubeService)
        {
            // Broadcast events belong to the main window (requests/settings page).
            SendMessage = json => _page.SendTo("index.html", json),
            RunOnUi = RunOnUi,
            PostToUi = PostToUi,
            QuitRequested = () => Application.Current?.Quit(),
        };

        // Each page gets its own response channel — the desktop does the same with
        // a per-window response target, and the pages rely on it (request ids).
        page.HostMessageReceived += OnHostMessage;

#if ANDROID
        // In-app YouTube sign-in: the app hosts the login and harvests its cookies
        // (a phone has no browser profile to import from).
        Interop.SignInAsync = () => AndroidSignInPage.ShowAsync();
        Interop.AuthBrowsersProvider = () => new List<string>();

        // Keep downloads alive in the background, with a progress notification.
        Interop.DownloadActivityChanged = (active, title) =>
        {
            var context = Android.App.Application.Context;
            if (active) DownloadService.OnDownloadStarted(context, title);
            else DownloadService.OnDownloadFinished(context);
        };
        Interop.DownloadProgressChanged = progress => DownloadService.OnDownloadProgress(Android.App.Application.Context, progress);

        // Library import: pick files/folders with the platform pickers and copy
        // them into the app sandbox, which is what the scanner can read.
        Interop.PickFilesAsync = title => AndroidFilePickers.PickFilesAsync(title);
        Interop.PickFolderAsync = title => AndroidFilePickers.PickFolderAsync(title);
        Interop.PrepareImportFiles = files => AndroidFilePickers.PrepareFilesAsync(files);
        Interop.PrepareImportFolder = uri => AndroidFilePickers.PrepareFolderAsync(uri);

        // The soundboard editor picks a single file; it needs a real path too.
        Interop.PrepareSoundFile = picked => AndroidFilePickers.PrepareSoundFileAsync(picked);

        // The legacy synchronous dialog (queueing loose files) can only block off
        // the UI thread; the library import above is the supported path.
        Interop.ShowOpenFileDialog = (title, _, _) =>
            MainThread.IsMainThread
                ? null
                : AndroidFilePickers.PickFilesAsync(title).GetAwaiter().GetResult();
#endif

        RemoteControlService.Instance.RegisterMusicEngine(Host.Music);
        AppBootstrap.Initialize();

        // OS media controls (lock screen, notification, headset/Bluetooth) —
        // the desktop's window does the same for MPRIS/SMTC.
        try { Interop.StartSystemMediaIntegration(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Media session failed: {ex.Message}"); }
    }

    private void OnHostMessage(string file, string message)
    {
        try
        {
            // Shell-level messages of the touch music page (which tab it should
            // show, the queue sidebar, the navigation-bar colours). They belong to
            // the mobile shell, not to the shared interop.
            if (TryHandleShellMessage(file, message)) return;

            Interop.HandleMessage(message, json => RunOnUi(() => _page.SendTo(file, json)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Interop failed for {file}: {ex}");
        }
    }

    private bool TryHandleShellMessage(string file, string message)
    {
        Newtonsoft.Json.Linq.JObject request;
        try { request = Newtonsoft.Json.Linq.JObject.Parse(message); }
        catch { return false; }

        if (request["type"]?.ToString() != "request") return false;

        var method = request["method"]?.ToString();
        object? result;

        switch (method)
        {
            case "mobileReady":
                result = new { tab = _page.CurrentTabKey, mode = _page.Mode };
                break;

            case "mobileOpenPlayer":
                _page.Select("player");
                result = new { success = true };
                break;

            case "mobileQueueClosed":
                _page.RestoreBaseTab();
                result = new { success = true };
                break;

            case "setNavColors":
                _page.SetNavColors(
                    request["primary"]?.ToString(),
                    request["secondary"]?.ToString());
                result = new { success = true };
                break;

            case "stageStatus":
            {
#if ANDROID
                result = StageMirror.StatusObject();
#else
                result = new { running = false };
#endif
                break;
            }

            case "stageStart":
            {
#if ANDROID
                StageMirror.Publish();
                result = StageMirror.StatusObject();
#else
                result = new { running = false, error = "The stage is Android-only." };
#endif
                break;
            }

            case "stageStop":
            {
#if ANDROID
                StageMirror.Stop();
                result = StageMirror.StatusObject();
#else
                result = new { running = false };
#endif
                break;
            }

            default:
                return false;
        }

        var id = request["id"];
        if (id != null)
        {
            var response = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = "response", id, result });
            RunOnUi(() => _page.SendTo(file, response));
        }

        return true;
    }

    private static void RunOnUi(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }

    private static void PostToUi(Action action) => MainThread.BeginInvokeOnMainThread(action);

    public void Dispose()
    {
        try { Interop.QuitRequested = null; } catch { }
        try { AppBootstrap.Shutdown(); } catch { }
    }
}
