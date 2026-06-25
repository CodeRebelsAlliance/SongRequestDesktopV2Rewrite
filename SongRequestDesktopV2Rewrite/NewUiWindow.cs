using Photino.NET;
using System.IO;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

public class NewUiWindow
{
    private PhotinoWindow? _window;
    private Thread? _thread;
    private readonly YoutubeFormInterop _interop;
    private readonly YoutubeForm _ytForm;
    private Action? _onDataRefreshedHandler;

    public bool IsOpen => _window != null;

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
            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "newui", "index.html");

            var w = new PhotinoWindow()
                .SetTitle("SongRequest V2")
                .SetSize(1280, 761)
                .Center()
                .RegisterWebMessageReceivedHandler((sender, message) =>
                {
                    _interop.HandleMessage(message);
                })
                .Load(htmlPath);

            _interop.SendMessage = (json) =>
            {
                try { w.Invoke(() => w.SendWebMessage(json)); } catch { }
            };
            _onDataRefreshedHandler = () =>
            {
                try { w.Invoke(() => _interop.SendEvent("refresh", null)); } catch { }
            };
            _ytForm.OnDataRefreshed += _onDataRefreshedHandler;
            w.WindowClosing += (sender, args) =>
            {
                _window = null;
                _interop.SendMessage = null;
                if (_onDataRefreshedHandler != null)
                    _ytForm.OnDataRefreshed -= _onDataRefreshedHandler;
                _interop.Dispose();
                return true;
            };

            _window = w;
            _ytForm.Dispatcher.Invoke(() => _ytForm.Hide());
            w.WaitForClose();
            _window = null;
        });

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    public void Close()
    {
        var w = _window;
        if (w != null)
        {
            w.Close();
            _window = null;
        }
    }
}
