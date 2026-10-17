using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Windows system-tray icon implemented with Shell_NotifyIcon on a dedicated
/// hidden-window message-loop thread.
///
/// NOTE: this code cannot be exercised on non-Windows CI, so it is compile-checked
/// only here; the menu/logic parts that can be are covered by unit tests
/// (<see cref="TrayMenu"/> and <see cref="NullTrayIcon"/>).
/// </summary>
internal sealed class Win32TrayIcon : ITrayIcon
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x0000;
    private const int NIM_DELETE = 0x0002;
    private const int NIF_MESSAGE = 0x0001;
    private const int NIF_ICON = 0x0002;
    private const int NIF_TIP = 0x0004;

    private const int MF_STRING = 0x0000;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;
    private const int TPM_NONOTIFY = 0x0080;

    private const int IDI_APPLICATION = 32512;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly string _tooltip;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private WndProc? _wndProc; // keep the delegate alive for the window class
    private string _className = string.Empty;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _icon = IntPtr.Zero;

    public bool IsSupported => true;

    public event EventHandler<TrayCommand>? CommandInvoked;

    public Win32TrayIcon(string tooltip)
    {
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "SongRequest V2" : tooltip;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "songrequest-tray" };
        _thread.Start();
        _ready.Wait(3000);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = BuildNotifyData();
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA BuildNotifyData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void MessageLoop()
    {
        _wndProc = WndProcImpl;
        var hInstance = GetModuleHandleW(null);
        _className = "SongRequestTray_" + Guid.NewGuid().ToString("N");

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassExW(ref windowClass);

        _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        _icon = LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (!string.IsNullOrEmpty(_className)) UnregisterClassW(_className, hInstance);
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_TRAYICON:
                int evt = (int)lParam;
                if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK)
                {
                    CommandInvoked?.Invoke(this, TrayCommand.Show);
                }
                else if (evt == WM_RBUTTONUP)
                {
                    ShowContextMenu(hWnd);
                }
                return IntPtr.Zero;

            case WM_COMMAND:
                CommandInvoked?.Invoke(this, (TrayCommand)(int)wParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                var data = BuildNotifyData();
                Shell_NotifyIconW(NIM_DELETE, ref data);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        var menu = CreatePopupMenu();
        foreach (var item in TrayMenu.Build())
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)(int)item.Command, item.Label);
        }

        GetCursorPos(out var point);
        SetForegroundWindow(hWnd);
        int selected = TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
            point.X, point.Y, 0, hWnd, IntPtr.Zero);
        PostMessageW(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        if (selected > 0)
        {
            CommandInvoked?.Invoke(this, (TrayCommand)selected);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        try { _thread.Join(1000); } catch { }
        _ready.Dispose();
    }

    // ── Interop ────────────────────────────────────────────────────────────

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, int flags, UIntPtr id, string item);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
