using Photino.NET;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

public class NewUiWindow
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
    private const int SwRestore = 9;

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

    private PhotinoWindow? _window;
    private PhotinoWindow? _musicPlayerWindow;
    private PhotinoWindow? _presentationWindow;
    private PhotinoWindow? _soundboardWindow;
    private PhotinoWindow? _visualizationWindow;
    private PhotinoWindow? _announcementWindow;

    private ITrayIcon? _tray;
    private volatile bool _forceClose;
    private bool _webViewReady;

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

            w.WindowCreated += (_, _) => BringToFront(w);

            w.WindowClosing += (_, _) =>
            {
                // Close-to-tray: cancel the close and hide until the user picks Quit.
                if (!_forceClose && _tray is { IsSupported: true } && ConfigService.Instance.Current?.CloseToTray == true)
                {
                    HideWindow(w);
                    return false;
                }

                _forceClose = true;
                try { _tray?.Dispose(); } catch { }
                _tray = null;

                _window = null;
                _musicPlayerWindow = null;
                _presentationWindow = null;
                _soundboardWindow = null;
                _visualizationWindow = null;
                _announcementWindow = null;

                _interop.SendMessage = null;
                SoundboardSendMessage = null;
                VisualizationSendMessage = null;
                AnnouncementSendMessage = null;

                if (_onDataRefreshedHandler != null)
                    _ytForm.OnDataRefreshed -= _onDataRefreshedHandler;

                _interop.Dispose();

                return true;
            };

            w.WindowMinimized += (_, _) =>
            {
                if (_tray is { IsSupported: true } && ConfigService.Instance.Current?.MinimizeToTray == true)
                    HideWindow(w);
            };

            w.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                // The webview is alive and rendering: the startup did not crash.
                if (!_webViewReady)
                {
                    _webViewReady = true;
                    StartupGuard.Complete();
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

            TrySetupTray(w);

            // Expose playback to the OS media controls (MPRIS/SMTC/...).
            try { _interop.StartSystemMediaIntegration(w.WindowHandle); } catch { }

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

    private void TrySetupTray(PhotinoWindow w)
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            var wantsTray = cfg?.CloseToTray == true || cfg?.MinimizeToTray == true;
            if (!wantsTray || !TrayIconFactory.IsSupported) return;

            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SRshortLogo.png");
            var tray = TrayIconFactory.Create("SongRequest V2", iconPath);
            if (!tray.IsSupported)
            {
                tray.Dispose();
                return;
            }

            tray.CommandInvoked += (_, command) => HandleTrayCommand(w, command);
            tray.Show();
            _tray = tray;
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
                    _forceClose = true;
                    try { _tray?.Dispose(); } catch { }
                    _tray = null;
                    try { w.Close(); } catch { }
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
                return;

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "musicplayer.html");

            var mp = new PhotinoWindow()
                .SetTitle("Music Player - SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .Load(htmlPath);

            mp.WindowCreated += (_, _) => BringToFront(mp);

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

            mp.WindowClosing += (_, _) =>
            {
                _musicPlayerWindow = null;
                MusicPlayerSendMessage = null;
                return true;
            };

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
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            var mp = _musicPlayerWindow;
            if (mp == null)
                return;

            _musicPlayerWindow = null;

            try
            {
                mp.Close();
            }
            catch { }
        });
    }

    public void ShowPresentationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_presentationWindow != null)
                return;

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

            pw.WindowCreated += (_, _) => BringToFront(pw);

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

            pw.WindowClosing += (_, _) =>
            {
                _presentationClosing = true;
                _presentationWindow = null;
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

                return true;
            };

            _presentationWindow = pw;

            pw.WaitForClose();
        });
    }

    public void ClosePresentationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            var pw = _presentationWindow;
            if (pw == null)
                return;

            _presentationWindow = null;

            try
            {
                pw.Close();
            }
            catch { }
        });
    }

    public void ShowSoundboardWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_soundboardWindow != null)
                return;

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "soundboard.html");

            var sb = new PhotinoWindow()
                .SetTitle("Soundboard - SongRequest V2")
                .SetSize(1100, 761)
                .Center()
                .Load(htmlPath);

            sb.WindowCreated += (_, _) => BringToFront(sb);

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

            sb.WindowClosing += (_, _) =>
            {
                _soundboardWindow = null;
                SoundboardSendMessage = null;
                try { _interop.OnSoundboardClosed(); } catch { }
                return true;
            };

            _soundboardWindow = sb;

            sb.WaitForClose();
        });
    }

    public void CloseSoundboardWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            var sb = _soundboardWindow;
            if (sb == null)
                return;

            _soundboardWindow = null;

            try
            {
                sb.Close();
            }
            catch { }
        });
    }

    public void ShowVisualizationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_visualizationWindow != null)
                return;

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "visualization.html");

            var vw = new PhotinoWindow()
                .SetTitle("Visualization - SongRequest V2")
                .SetSize(1000, 720)
                .Center()
                .Load(htmlPath);

            vw.WindowCreated += (_, _) => BringToFront(vw);

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
                try
                {
                    vw.Invoke(() => vw.SendWebMessage(json));
                }
                catch { }
            };

            vw.WindowClosing += (_, _) =>
            {
                _visualizationWindow = null;
                VisualizationSendMessage = null;
                try { _interop.OnVisualizationClosed(); } catch { }
                return true;
            };

            _visualizationWindow = vw;

            vw.WaitForClose();
        });
    }

    public void CloseVisualizationWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            var vw = _visualizationWindow;
            if (vw == null)
                return;

            _visualizationWindow = null;

            try
            {
                vw.Close();
            }
            catch { }
        });
    }

    public void ShowAnnouncementWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            if (_announcementWindow != null)
                return;

            var htmlPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "newui",
                "announcement.html");

            var aw = new PhotinoWindow()
                .SetTitle("Announcement - SongRequest V2")
                .SetSize(460, 560)
                .Center()
                .Load(htmlPath);

            aw.WindowCreated += (_, _) => BringToFront(aw);

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

            aw.WindowClosing += (_, _) =>
            {
                _announcementWindow = null;
                AnnouncementSendMessage = null;
                try { _interop.OnAnnouncementClosed(); } catch { }
                return true;
            };

            _announcementWindow = aw;

            aw.WaitForClose();
        });
    }

    public void CloseAnnouncementWindow()
    {
        var mainWindow = _window;
        if (mainWindow == null)
            return;

        mainWindow.Invoke(() =>
        {
            var aw = _announcementWindow;
            if (aw == null)
                return;

            _announcementWindow = null;

            try
            {
                aw.Close();
            }
            catch { }
        });
    }

    public void Close()
    {
        var w = _window;
        if (w != null)
        {
            try
            {
                w.Close();
            }
            catch { }
        }

        _window = null;
        _musicPlayerWindow = null;
        _presentationWindow = null;
    }
}
