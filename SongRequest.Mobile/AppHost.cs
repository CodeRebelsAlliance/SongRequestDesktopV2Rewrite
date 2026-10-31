using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using SongRequestDesktopV2Rewrite;
using SongRequestDesktopV2Rewrite.Integration;

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
            SendMessage = json =>
            {
                if (!_page.SendTo(MainPage.MobileRequestsFile, json))
                    _page.SendTo("index.html", json);
            },
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
        Interop.PickSoundFile = title => AndroidFilePickers.PickSoundFileAsync(title);
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
        RegisterCarMediaBridge();

        if (CarModeState.IsCarMode) ApplyCarMode();

        // Optional local API + MCP server (off by default, see Settings).
        AppIntegrationServer.Instance.Initialize(Host, Interop);
        AppIntegrationServer.Instance.RegisterPlatformCommandHandler(HandleIntegrationPlatformCommandAsync);

        // OS media controls (lock screen, notification, headset/Bluetooth) —
        // the desktop's window does the same for MPRIS/SMTC.
        try { Interop.StartSystemMediaIntegration(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Media session failed: {ex.Message}"); }
    }

    /// <summary>
    /// Host-specific commands for the local API/MCP servers. The stage server
    /// lives in the mobile host (it is Android-only), so it is exposed here and
    /// falls through to the shared interop for every other command.
    /// </summary>
    private static Task<Newtonsoft.Json.Linq.JToken?> HandleIntegrationPlatformCommandAsync(
        string method,
        Newtonsoft.Json.Linq.JObject? arguments,
        System.Threading.CancellationToken cancellationToken)
    {
#if ANDROID
        switch (method)
        {
            case "stageStatus":
                return MainThread.InvokeOnMainThreadAsync(
                    () => (Newtonsoft.Json.Linq.JToken?)Newtonsoft.Json.Linq.JObject.FromObject(StageMirror.StatusObject()));

            case "stageStart":
                return MainThread.InvokeOnMainThreadAsync(() =>
                {
                    StageMirror.Publish();
                    return (Newtonsoft.Json.Linq.JToken?)Newtonsoft.Json.Linq.JObject.FromObject(StageMirror.StatusObject());
                });

            case "stageStop":
                return MainThread.InvokeOnMainThreadAsync(() =>
                {
                    StageMirror.Stop();
                    return (Newtonsoft.Json.Linq.JToken?)Newtonsoft.Json.Linq.JObject.FromObject(StageMirror.StatusObject());
                });
        }
#endif
        return Task.FromResult<Newtonsoft.Json.Linq.JToken?>(null);
    }

    private string? _modeBeforeCar;

    private void RegisterCarMediaBridge()
    {
        CarMediaBridge.Register(
            browseLibrary: () =>
            {
                try
                {
                    return Interop.LibraryService.Data.Songs
                        .Where(s => s.FileStatus == FileStatus.Present)
                        .Select(LibraryEntry)
                        .ToList();
                }
                catch { return new List<CarMediaEntry>(); }
            },
            browseQueue: () =>
            {
                try
                {
                    return Host.Music.Queue
                        .Select((song, index) => new CarMediaEntry(
                            "queue:" + index,
                            song.Title,
                            song.Artist,
                            "",
                            song.thumbnail as string,
                            (long)song.Duration.TotalMilliseconds))
                        .ToList();
                }
                catch { return new List<CarMediaEntry>(); }
            },
            search: query =>
            {
                try
                {
                    return Interop.LibraryService.Data.Search(query ?? string.Empty, 50)
                        .Select(LibraryEntry)
                        .ToList();
                }
                catch { return new List<CarMediaEntry>(); }
            },
            play: PlayCarMediaId);
        CarMediaBridge.SetReady(Host.SetupComplete);
    }

    private static CarMediaEntry LibraryEntry(LibrarySong song) => new(
        "library:" + song.Id,
        song.Title,
        song.Artist,
        song.Album,
        song.ThumbnailPath,
        (long)song.Duration.TotalMilliseconds);

    private bool PlayCarMediaId(string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId)) return false;

        if (mediaId.StartsWith("library:", StringComparison.Ordinal))
        {
            var id = mediaId["library:".Length..];
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var lib = Interop.LibraryService.Data.FindSong(id);
                    if (lib == null || !File.Exists(lib.FilePath)) return;
                    var song = new Song(lib.Title, lib.Artist, lib.ThumbnailPath, lib.Duration, lib.FilePath);
                    Host.Music.ClearQueue();
                    Host.Music.Queue.Add(song);
                    Host.Music.ComputeQueueTimings();
                    Host.Music.PlaySong(song);
                }
                catch { }
            });
            return true;
        }

        if (mediaId.StartsWith("queue:", StringComparison.Ordinal) &&
            int.TryParse(mediaId["queue:".Length..], out int index))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    while (index-- > 0 && Host.Music.Queue.Count > 1)
                        Host.Music.RemoveQueueItem(0);
                    if (Host.Music.Queue.Count > 0) Host.Music.PlaySong(Host.Music.Queue[0]);
                }
                catch { }
            });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Android car mode / Android Auto connection: switch to the music-only
    /// layout and remember the previous mode for when the car disconnects.
    /// </summary>
    public void ApplyCarMode()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(ApplyCarMode);
            return;
        }

        if (CarModeState.IsCarMode)
        {
            _modeBeforeCar ??= ConfigService.Instance.Current?.OperatingMode ?? "SongRequests";
            ConfigService.Instance.Update(c => c.OperatingMode = StartupMode.MusicPlayer.ToString());
            _page.EnableCarMode();
        }
        else if (_modeBeforeCar != null)
        {
            var previous = _modeBeforeCar;
            _modeBeforeCar = null;
            ConfigService.Instance.Update(c => c.OperatingMode = previous);
            _page.DisableCarMode(previous);
        }
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
                result = new { tab = _page.CurrentTabKey, mode = _page.Mode, car = _page.IsCarMode };
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
