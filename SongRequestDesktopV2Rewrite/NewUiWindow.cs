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

    private PhotinoWindow? _window;
    private PhotinoWindow? _musicPlayerWindow;
    private PhotinoWindow? _presentationWindow;

    private Thread? _thread;

    private readonly YoutubeFormInterop _interop;
    private readonly YoutubeForm _ytForm;

    private Action? _onDataRefreshedHandler;
    private volatile bool _presentationClosing;

    public bool IsOpen => _window != null;

    public Action<string>? MusicPlayerSendMessage { get; set; }
    public Action<string>? PresentationSendMessage { get; set; }

    public LibraryService? LibraryService => _interop.LibraryService;

    public void SendEvent(string eventName, object? data) => _interop.SendEvent(eventName, data);

    public NewUiWindow(YoutubeFormInterop interop, YoutubeForm ytForm)
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
                try
                {
                    if (w.WindowHandle != IntPtr.Zero)
                        SetForegroundWindow(w.WindowHandle);
                }
                catch { }
            };

            w.WindowClosing += (_, _) =>
            {
                _window = null;
                _musicPlayerWindow = null;
                _presentationWindow = null;

                _interop.SendMessage = null;

                if (_onDataRefreshedHandler != null)
                    _ytForm.OnDataRefreshed -= _onDataRefreshedHandler;

                _interop.Dispose();

                return true;
            };

            w.RegisterWebMessageReceivedHandler((sender, message) =>
            {
                _interop.HandleMessage(message);
            });

            w.SetTitle("SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .Load(htmlPath);

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

            _ytForm.Dispatcher.Invoke(() => _ytForm.Hide());

            w.WaitForClose();

            _window = null;
        });

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
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

            mp.WindowCreated += (_, _) =>
            {
                try
                {
                    if (mp.WindowHandle != IntPtr.Zero)
                        SetForegroundWindow(mp.WindowHandle);
                }
                catch { }
            };

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

            pw.WindowCreated += (_, _) =>
            {
                try
                {
                    if (pw.WindowHandle != IntPtr.Zero)
                        SetForegroundWindow(pw.WindowHandle);
                }
                catch { }
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
