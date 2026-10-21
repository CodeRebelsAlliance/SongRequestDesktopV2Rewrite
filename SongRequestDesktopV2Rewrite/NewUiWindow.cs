using Photino.NET;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

public class NewUiWindow : IUiShell
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
    private const int SwRestore = 9;

    /// <summary>
    /// Hides a sub-window instead of destroying it.
    ///
    /// Tearing down a WebKitGTK view blocks the GTK thread on some setups (the window
    /// stays on screen and the whole UI stops handling messages), so "closing" a
    /// sub-window only hides it; reopening it is then instant because the web view is
    /// already loaded. No-op elsewhere.
    ///
    /// A window that is only hidden still has to be *prevented* from closing — see
    /// <see cref="RegisterHideOnClose"/> (returning false there quits the whole app).
    /// </summary>
    private static void SetSubWindowVisible(string titlePrefix, bool visible)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (visible) GtkWindows.Show(titlePrefix);
            else GtkWindows.Hide(titlePrefix);
        }
        catch { }
    }

    /// <summary>
    /// Registers the "close" behaviour of a sub-window: run <paramref name="onClosing"/>
    /// (channel bookkeeping), hide the window and — crucially — <b>return true</b>.
    ///
    /// Photino's <c>NetClosingDelegate</c> reads a true result as "do not close", so
    /// returning true is what keeps the GTK widget alive; returning false lets GTK
    /// destroy it, and Photino hooks every toplevel window's "destroy" signal to
    /// <c>gtk_main_quit()</c> — which stops the single GTK main loop and takes the
    /// whole application down (closing any sub-window used to quit the app).
    /// </summary>
    private static void RegisterHideOnClose(PhotinoWindow window, string titlePrefix, Action onClosing)
    {
        window.WindowClosing += (_, _) =>
        {
            try { onClosing(); } catch { }
            SetSubWindowVisible(titlePrefix, false);
            return true;
        };
    }

    /// <summary>Brings a Photino window to the foreground (Windows only; no-op elsewhere).</summary>
    private static void BringToFront(PhotinoWindow window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (window.WindowHandle != IntPtr.Zero)
                SetForegroundWindow(window.WindowHandle);
        }
        catch { }
    }

    /// <summary>
    /// Re-applies a window's size once it exists.
    ///
    /// GTK — and Wayland compositors in particular — ignore a size requested before
    /// the window is realised, so a window created with <c>SetSize</c> alone comes up
    /// at Photino's 200x200 default and the user has to resize it by hand.
    /// </summary>
    private static void ApplyInitialGeometry(PhotinoWindow window, int width, int height)
    {
        try
        {
            window.SetSize(width, height);
            window.Center();
        }
        catch { }
    }

    /// <summary>
    /// Applies the application icon. Windows uses the window icon; on Linux the GTK
    /// default icon is set and a user-level <c>.desktop</c> entry is installed, which
    /// is the only thing Wayland compositors use to resolve a window's icon.
    /// </summary>
    private static void ApplyWindowIcon(PhotinoWindow window)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (OperatingSystem.IsWindows())
            {
                var ico = Path.Combine(baseDir, "logo.ico");
                if (File.Exists(ico)) window.IconFile = ico;
            }
            else if (OperatingSystem.IsLinux())
            {
                var png = Path.Combine(baseDir, LinuxDesktopIntegration.IconFileName);
                LinuxDesktopIntegration.ApplyWindowIcon(png);
                LinuxDesktopIntegration.EnsureApplicationEntry(AutoStart.LauncherPath(), png);
            }
        }
        catch
        {
            // Icons are cosmetic; never let them break startup.
        }
    }

    private PhotinoWindow? _window;
    private PhotinoWindow? _musicPlayerWindow;
    private PhotinoWindow? _presentationWindow;
    private PhotinoWindow? _soundboardWindow;
    private PhotinoWindow? _visualizationWindow;
    private PhotinoWindow? _announcementWindow;

    private ITrayIcon? _tray;
    private volatile bool _forceClose;
    private bool _webViewReady;
    private bool _traySetupDone;
    private Action<string>? _musicPlayerSend;
    private Action<string>? _presentationSend;
    private Action<string>? _soundboardSend;
    private Action<string>? _visualizationSend;
    private Action<string>? _announcementSend;

    private Thread? _thread;

    private readonly YoutubeFormInterop _interop;
    private readonly ISongRequestHost _ytForm;

    private Action? _onDataRefreshedHandler;
    private volatile bool _presentationClosing;

    public bool IsOpen => _window != null;

    public Action<string>? MusicPlayerSendMessage { get; set; }
    public Action<string>? PresentationSendMessage { get; set; }
    public Action<string>? SoundboardSendMessage { get; set; }
    public Action<string>? VisualizationSendMessage { get; set; }
    public Action<string>? AnnouncementSendMessage { get; set; }

    public LibraryService? LibraryService => _interop.LibraryService;

    public void SendEvent(string eventName, object? data) => _interop.SendEvent(eventName, data);

    /// <summary>Dispatches work onto the Photino UI thread (queue/artwork refreshes).</summary>
    public void PostToUi(Action action)
    {
        var window = _window;
        if (window == null)
        {
            action();
            return;
        }

        try { window.Invoke(action); }
        catch { }
    }

    public NewUiWindow(YoutubeFormInterop interop, ISongRequestHost ytForm)
    {
        _interop = interop;
        _ytForm = ytForm;
    }

    public void Show()
    {
        if (_window != null)
            return;

        _thread = new Thread(() =>
        {
            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "index.html");

            var w = new PhotinoWindow();

            w.WindowCreated += (_, _) =>
            {
                // GTK (Wayland in particular) drops a size requested before the window
                // is realised — it comes up at Photino's 200x200 default — so the
                // geometry is applied again now that the window exists.
                ApplyInitialGeometry(w, 1280, 761);
                ApplyWindowIcon(w);
                BringToFront(w);
            };

            w.WindowClosing += (_, _) =>
            {
                // Close-to-tray: cancel the close and hide until the user picks Quit.
                // true = "do not close" (Photino's NetClosingDelegate); returning false
                // would destroy the window — and Photino hooks every toplevel's GTK
                // "destroy" to gtk_main_quit(), which ends the app.
                if (!_forceClose && _tray is { IsSupported: true } && ConfigService.Instance.Current?.CloseToTray == true)
                {
                    HideWindow(w);
                    return true;
                }

                // Quit() releases the OS-visible resources and calls Environment.Exit,
                // so this never returns; cancelling is only a safety net if it does.
                Quit(w);
                return true;
            };

            w.WindowMinimized += (_, _) =>
            {
                if (_tray is { IsSupported: true } && ConfigService.Instance.Current?.MinimizeToTray == true)
                    HideWindow(w);
            };

            w.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                // The webview is alive and rendering. The startup marker is only cleared
                // once the tray has been set up too (see ScheduleTraySetup), so a native
                // crash while creating the tray still counts as an unfinished start.
                if (!_webViewReady)
                {
                    _webViewReady = true;
                    if (_traySetupDone) StartupGuard.Complete();
                }

                _interop.HandleMessage(message);
            });

            w.SetTitle("SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .Load(htmlPath);

            // Native (cross-platform) open-file dialog for the interop.
            _interop.ShowOpenFileDialog = (title, pattern, multiSelect) =>
            {
                try
                {
                    var filters = new[] { (title, pattern.Split(';')) };
                    var result = w.ShowOpenFile(title, "", multiSelect, filters);
                    return result != null && result.Length > 0 ? result : null;
                }
                catch { return null; }
            };

            _interop.SendMessage = json =>
            {
                try
                {
                    w.Invoke(() => w.SendWebMessage(json));
                }
                catch { }
            };

            // The UI's Quit buttons run the same shutdown as the tray/close paths.
            _interop.QuitRequested = () =>
            {
                try { w.Invoke(() => Quit(w)); }
                catch { Quit(w); }
            };

            _onDataRefreshedHandler = () =>
            {
                try
                {
                    w.Invoke(() => _interop.SendEvent("refresh", null));
                }
                catch { }
            };

            _ytForm.OnDataRefreshed += _onDataRefreshedHandler;

            _window = w;

            _ytForm.HideHostWindow();

            // The tray is created from the GTK main loop once the window is fully
            // constructed — creating AppIndicator/GTK widgets from Photino's window
            // callback (while GTK is still setting the window up) segfaults GTK.
            ScheduleTraySetup(w);

            // Expose playback to the OS media controls (MPRIS/SMTC/...). Photino's
            // WindowHandle is Windows-only and *throws* on Linux, which used to abort
            // this call before the session was created — so MPRIS never started.
            try { _interop.StartSystemMediaIntegration(w.WindowHandle); }
            catch { _interop.StartSystemMediaIntegration(); }

            if (AppLaunchArgs.StartMinimized || ConfigService.Instance.Current?.StartMinimized == true)
            {
                if (_tray is { IsSupported: true } && ConfigService.Instance.Current?.MinimizeToTray == true)
                    HideWindow(w);
                else
                    try { w.SetMinimized(true); } catch { }
            }

            w.WaitForClose();

            _window = null;
        });

        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    /// <summary>Starts the Photino UI and blocks the calling thread until it closes.</summary>
    public void RunBlocking()
    {
        Show();
        try { _thread?.Join(); } catch { }
    }

    /// <summary>
    /// Quits the application for real (used by the Quit buttons, the tray menu and
    /// the window's X button).
    ///
    /// Closing the window alone is not enough: on Linux Photino's GTK/WebKit teardown
    /// aborts (<c>GdkWindow … unexpectedly destroyed</c>) and non-background native
    /// threads — the D-Bus read loop, GTK — can keep the process alive, which is why
    /// the window could close while the app kept running in the background. So the
    /// resources the OS can see (tray icon, MPRIS/SMTC now-playing, MIDI devices) are
    /// released first and the process is then terminated without running the fragile
    /// native teardown.
    /// </summary>
    public void Quit(PhotinoWindow? window = null)
    {
        _forceClose = true;

        try { _tray?.Dispose(); } catch { }
        _tray = null;

        try { _interop.SendMessage = null; } catch { }
        SoundboardSendMessage = null;
        VisualizationSendMessage = null;
        AnnouncementSendMessage = null;

        // Releases the OS media session (so the desktop drops "now playing") and the
        // MIDI devices/remote control.
        try { _interop.Dispose(); } catch { }

        _window = null;
        _musicPlayerWindow = null;
        _presentationWindow = null;
        _soundboardWindow = null;
        _visualizationWindow = null;
        _announcementWindow = null;

        Program.ExitApplication();
    }

    /// <summary>Writes a diagnostic line to the console and to the app's log file.</summary>
    private static void ConfigurationLog(string message)
    {
        try { Console.WriteLine("[SongRequest] " + message); } catch { }
    }

    /// <summary>
    /// Creates the tray icon a moment after the window is up, on the GTK thread.
    ///
    /// Building GTK widgets (the AppIndicator menu) inside Photino's window-created
    /// callback crashes GTK — the style provider is not ready yet — so the work is
    /// deferred until the window is fully constructed and then marshalled through
    /// <see cref="PhotinoWindow.Invoke"/>.
    /// </summary>
    private void ScheduleTraySetup(PhotinoWindow window)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
            try
            {
                window.Invoke(() =>
                {
                    TrySetupTray(window);

                    // "Start minimised" only hides to the tray once the tray exists.
                    var cfg = ConfigService.Instance.Current;
                    if ((AppLaunchArgs.StartMinimized || cfg?.StartMinimized == true)
                        && _tray is { IsSupported: true }
                        && cfg?.MinimizeToTray == true)
                    {
                        HideWindow(window);
                    }

                    _traySetupDone = true;
                    if (_webViewReady) StartupGuard.Complete();
                });
            }
            catch
            {
                // The window may already be gone.
            }
        });
    }

    private void TrySetupTray(PhotinoWindow w)
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            var wantsTray = cfg?.CloseToTray == true || cfg?.MinimizeToTray == true;
            if (!wantsTray)
            {
                StartupGuard.ClearTrayUsed();
                return;
            }

            if (!TrayIconFactory.IsSupported) return;

            // A GTK/AppIndicator crash here is native and cannot be caught, so if the
            // last starts never got as far as the webview, skip the tray this time and
            // remember it (the user can re-enable it later; the app must stay launchable).
            if (StartupGuard.TrayDisabled
                || (StartupGuard.CrashRecoveryThisRun && StartupGuard.TrayUsedLastRun))
            {
                // Creating the tray crashed a previous start (a native GTK/AppIndicator
                // abort the app cannot catch), so leave it off until the user re-enables
                // it under Settings → Startup & Window.
                StartupGuard.DisableTray();
                ConfigurationLog("tray: disabled after a previous crash - re-enable it under Settings -> Startup & Window");
                return;
            }

            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SRshortLogo.png");
            StartupGuard.NoteTrayUsed();
            var tray = TrayIconFactory.Create("SongRequest V2", iconPath);
            if (!tray.IsSupported)
            {
                tray.Dispose();
                StartupGuard.ClearTrayUsed();
                return;
            }

            tray.CommandInvoked += (_, command) => HandleTrayCommand(w, command);
            tray.Show();
            _tray = tray;
            ConfigurationLog("tray: created");
        }
        catch
        {
            // Tray is optional; never let it break startup.
        }
    }

    private void HandleTrayCommand(PhotinoWindow w, TrayCommand command)
    {
        void Act()
        {
            switch (command)
            {
                case TrayCommand.Show:
                    RestoreWindow(w);
                    break;
                case TrayCommand.TogglePlayPause:
                    _ytForm.Music.TogglePlayPause();
                    break;
                case TrayCommand.Next:
                    _ytForm.Music.Next();
                    break;
                case TrayCommand.Stop:
                    _ytForm.Music.Stop();
                    break;
                case TrayCommand.Exit:
                    Quit(w);
                    break;
            }
        }

        // Tray callbacks arrive on the tray thread; marshal onto the UI thread.
        try { w.Invoke(Act); }
        catch { try { Act(); } catch { } }
    }

    private static void HideWindow(PhotinoWindow w)
    {
        if (OperatingSystem.IsWindows())
        {
            try { ShowWindow(w.WindowHandle, SwHide); } catch { }
        }
        else
        {
            try { w.SetMinimized(true); } catch { }
        }
    }

    private static void RestoreWindow(PhotinoWindow w)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                ShowWindow(w.WindowHandle, SwRestore);
                SetForegroundWindow(w.WindowHandle);
            }
            catch { }
        }
        else
        {
            try { w.SetMinimized(false); } catch { }
        }
    }

    public void ShowMusicPlayerWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        // Dispatch to the Photino STA thread — same thread as the main window.
        // PhotinoWindow MUST be created on an STA thread.
        mainWindow.Invoke(() =>
        {
            if (_musicPlayerWindow != null)
            {
                // The window is only hidden when "closed": show it again and resume
                // sending it data.
                SetSubWindowVisible("Music Player - ", true);
                MusicPlayerSendMessage = _musicPlayerSend;
                return;
            }

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "musicplayer.html");

            var mp = new PhotinoWindow()
                .SetTitle("Music Player - SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .Load(htmlPath);

            mp.WindowCreated += (_, _) => { ApplyInitialGeometry(mp, 1280, 761); BringToFront(mp); };

            mp.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                _interop.HandleMessage(message, json =>
                {
                    try
                    {
                        mp.Invoke(() => mp.SendWebMessage(json));
                    }
                    catch { }
                });
            });

            // Set up send channel targeting this music player window
            MusicPlayerSendMessage = json =>
            {
                try
                {
                    mp.Invoke(() => mp.SendWebMessage(json));
                }
                catch { }
            };

            _musicPlayerSend = MusicPlayerSendMessage;

            RegisterHideOnClose(mp, "Music Player - ", () => MusicPlayerSendMessage = null);

            _musicPlayerWindow = mp;

            // WaitForClose() calls Photino_ctor to create the native window,
            // then skips the message loop because _messageLoopIsStarted is
            // already true (set by the main window's WaitForClose).
            // The existing message loop pumps for both windows on this thread.
            mp.WaitForClose();
        });
    }

    public void CloseMusicPlayerWindow()
    {
        // The window instance is kept (hidden) so reopening it is instant and no
        // web-view teardown can block the UI thread.
        SetSubWindowVisible("Music Player - ", false);;
    }

    public void ShowPresentationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_presentationWindow != null)
            {
                // The window is only hidden when "closed": show it again and resume
                // sending it data. Its page may have missed the source switch while
                // hidden, so push the complete current state again.
                SetSubWindowVisible("Presentation - ", true);
                PresentationSendMessage = _presentationSend;
                _interop.RefreshPresentation();
                return;
            }

            _presentationClosing = false;

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "presentation.html");

            var pw = new PhotinoWindow()
                .SetTitle("Presentation - SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .Load(htmlPath);

            pw.WindowCreated += (_, _) =>
            {
                ApplyInitialGeometry(pw, 1280, 761);
                BringToFront(pw);
                // The "Open presentation in fullscreen mode" setting was stored but
                // never applied to the window.
                if (ConfigService.Instance.Current?.PresentationFullscreen == true)
                {
                    try { pw.SetFullScreen(true); } catch { }
                }
            };

            pw.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                if (_presentationClosing) return;
                _interop.HandleMessage(message, json =>
                {
                    if (_presentationClosing) return;
                    try
                    {
                        pw.Invoke(() => pw.SendWebMessage(json));
                    }
                    catch { }
                });
            });

            PresentationSendMessage = json =>
            {
                if (_presentationClosing) return;
                try
                {
                    pw.Invoke(() => pw.SendWebMessage(json));
                }
                catch { }
            };

            _presentationSend = PresentationSendMessage;

            RegisterHideOnClose(pw, "Presentation - ", () =>
            {
                _presentationClosing = true;
                PresentationSendMessage = null;

                // Notify the music player window so the button state updates
                try
                {
                    var mpSend = MusicPlayerSendMessage;
                    if (mpSend != null)
                    {
                        var json = "{\"type\":\"event\",\"eventName\":\"presentationClosed\"}";
                        mpSend(json);
                    }
                }
                catch { }
            });

            _presentationWindow = pw;

            pw.WaitForClose();
        });
    }

    public void ClosePresentationWindow()
    {
        // The window instance is kept (hidden) so reopening it is instant and no
        // web-view teardown can block the UI thread.
        SetSubWindowVisible("Presentation - ", false);;
    }

    public void ShowSoundboardWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_soundboardWindow != null)
            {
                // The window is only hidden when "closed": show it again and resume
                // sending it data.
                SetSubWindowVisible("Soundboard - ", true);
                SoundboardSendMessage = _soundboardSend;
                return;
            }

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "soundboard.html");

            var sb = new PhotinoWindow()
                .SetTitle("Soundboard - SongRequest V2")
                .SetSize(1100, 761)
                .Center()
                .Load(htmlPath);

            sb.WindowCreated += (_, _) => { ApplyInitialGeometry(sb, 1100, 761); BringToFront(sb); };

            sb.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                _interop.HandleMessage(message, json =>
                {
                    try
                    {
                        sb.Invoke(() => sb.SendWebMessage(json));
                    }
                    catch { }
                });
            });

            SoundboardSendMessage = json =>
            {
                try
                {
                    sb.Invoke(() => sb.SendWebMessage(json));
                }
                catch { }
            };

            _soundboardSend = SoundboardSendMessage;

            RegisterHideOnClose(sb, "Soundboard - ", () =>
            {
                SoundboardSendMessage = null;
                try { _interop.OnSoundboardClosed(); } catch { }
            });

            _soundboardWindow = sb;

            sb.WaitForClose();
        });
    }

    public void CloseSoundboardWindow()
    {
        // The window instance is kept (hidden) so reopening it is instant and no
        // web-view teardown can block the UI thread.
        SetSubWindowVisible("Soundboard - ", false);;
    }

    public void ShowVisualizationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_visualizationWindow != null)
            {
                // The window is only hidden when "closed": show it again and resume
                // sending it data.
                SetSubWindowVisible("Visualization - ", true);
                VisualizationSendMessage = _visualizationSend;
                // Closing disposed the streamer; restart it for the reopened window.
                try { _interop.StartVisualizationStreaming(); } catch { }
                return;
            }

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "visualization.html");

            var vw = new PhotinoWindow()
                .SetTitle("Visualization - SongRequest V2")
                .SetSize(1000, 720)
                .Center()
                .Load(htmlPath);

            // The audio thread starts pushing ~30 fps frames as soon as the
            // streamer is created, before WaitForClose() has created the native
            // window. Calling Invoke on that half-created window crashes WebView2
            // on Windows, so the send channel stays closed until WindowCreated.
            var visualizationReady = false;

            vw.WindowCreated += (_, _) =>
            {
                visualizationReady = true;
                ApplyInitialGeometry(vw, 1000, 720);
                BringToFront(vw);
                try { _interop.StartVisualizationStreaming(); } catch { }
            };

            vw.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                _interop.HandleMessage(message, json =>
                {
                    try
                    {
                        vw.Invoke(() => vw.SendWebMessage(json));
                    }
                    catch { }
                });
            });

            VisualizationSendMessage = json =>
            {
                // Never touch the native window before it exists: the audio
                // capture thread pushes frames immediately and WebView2 crashes
                // when Invoked during window construction on Windows.
                //
                // Only the ready flag is used here: Photino's WindowHandle throws
                // PlatformNotSupportedException on Linux, and the exception escaped
                // this lambda before its try block — silently dropping every frame
                // (the window stayed on "No audio playing"). WindowCreated already
                // guarantees the native window exists on every platform.
                if (!visualizationReady) return;
                try
                {
                    vw.Invoke(() => vw.SendWebMessage(json));
                }
                catch { }
            };

            _visualizationSend = VisualizationSendMessage;

            RegisterHideOnClose(vw, "Visualization - ", () =>
            {
                VisualizationSendMessage = null;
                try { _interop.OnVisualizationClosed(); } catch { }
            });

            _visualizationWindow = vw;
            vw.WaitForClose();
        });
    }

    public void CloseVisualizationWindow()
    {
        // The window instance is kept (hidden) so reopening it is instant and no
        // web-view teardown can block the UI thread.
        SetSubWindowVisible("Visualization - ", false);;
    }

    /// <summary>
    /// Applies fullscreen to a sub-window on behalf of its page.
    ///
    /// The pages used to call the DOM Fullscreen API, but that needs the embedder to
    /// handle WebKit's fullscreen request (Photino does not), so the button silently
    /// did nothing once the UI moved to WebKitGTK. This drives the native window
    /// instead, which works on every platform. Returns whether a window was found.
    /// </summary>
    /// <summary>
    /// Switches the operating mode from Settings. The desktop keeps its separate
    /// windows, so this just brings the mode's main window forward.
    /// </summary>
    public void SetOperatingMode(StartupMode mode)
    {
        try
        {
            switch (mode)
            {
                case StartupMode.MusicPlayer:
                case StartupMode.MusicShare:
                    ShowMusicPlayerWindow();
                    break;
                case StartupMode.Soundboard:
                    ShowSoundboardWindow();
                    break;
            }
        }
        catch { }
    }

    public bool SetSubWindowFullscreen(string? windowName, bool enabled)
    {
        var target = windowName switch
        {
            "visualization" => _visualizationWindow,
            "presentation" => _presentationWindow,
            "musicplayer" => _musicPlayerWindow,
            "soundboard" => _soundboardWindow,
            "announcement" => _announcementWindow,
            _ => null,
        };

        if (target == null) return false;

        try
        {
            target.Invoke(() => { try { target.SetFullScreen(enabled); } catch { } });
            return true;
        }
        catch
        {
            // Fall back to calling it directly when the invoke cannot be marshalled.
            try { target.SetFullScreen(enabled); return true; } catch { return false; }
        }
    }

    public void ShowAnnouncementWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_announcementWindow != null)
            {
                // The window is only hidden when "closed": show it again and resume
                // sending it data.
                SetSubWindowVisible("Announcement - ", true);
                AnnouncementSendMessage = _announcementSend;
                return;
            }

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "announcement.html");

            var aw = new PhotinoWindow()
                .SetTitle("Announcement - SongRequest V2")
                .SetSize(460, 560)
                .Center()
                .Load(htmlPath);

            aw.WindowCreated += (_, _) => { ApplyInitialGeometry(aw, 460, 560); BringToFront(aw); };

            aw.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                _interop.HandleMessage(message, json =>
                {
                    try
                    {
                        aw.Invoke(() => aw.SendWebMessage(json));
                    }
                    catch { }
                });
            });

            AnnouncementSendMessage = json =>
            {
                try
                {
                    aw.Invoke(() => aw.SendWebMessage(json));
                }
                catch { }
            };

            _announcementSend = AnnouncementSendMessage;

            RegisterHideOnClose(aw, "Announcement - ", () =>
            {
                AnnouncementSendMessage = null;
                try { _interop.OnAnnouncementClosed(); } catch { }
            });

            _announcementWindow = aw;

            aw.WaitForClose();
        });
    }

    public void CloseAnnouncementWindow()
    {
        // The window instance is kept (hidden) so reopening it is instant and no
        // web-view teardown can block the UI thread.
        SetSubWindowVisible("Announcement - ", false);;
    }

    /// <summary>
    /// Requests a full quit (used by the MPRIS <c>Quit</c> command). Routed through
    /// <see cref="Quit"/> so the process really exits instead of depending on
    /// Photino's native teardown.
    /// </summary>
    public void Close() => Quit(_window);
}
