using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Windows global hotkeys via <c>RegisterHotKey</c> on a hidden message-only
/// window. NOTE: not runnable on non-Windows CI — compile-checked only.
/// </summary>
internal sealed class WindowsGlobalHotkeyBackend : IGlobalHotkeyBackend
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly List<int> _ids = new();
    private WndProc? _wndProc;
    private string _className = string.Empty;
    private IntPtr _hwnd = IntPtr.Zero;

    public bool IsSupported => _hwnd != IntPtr.Zero;

    public event EventHandler<int>? HotkeyPressed;

    public WindowsGlobalHotkeyBackend()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "songrequest-hotkeys" };
        _thread.Start();
        _ready.Wait(3000);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the hotkey message window.");
    }

    public bool Register(int id, HotkeyGesture gesture)
    {
        if (_hwnd == IntPtr.Zero) return false;

        var vk = WindowsVirtualKeys.ToVirtualKey(gesture.Key);
        if (vk == null) return false;

        uint modifiers = MOD_NOREPEAT;
        if (gesture.Alt) modifiers |= MOD_ALT;
        if (gesture.Ctrl) modifiers |= MOD_CONTROL;
        if (gesture.Shift) modifiers |= MOD_SHIFT;
        if (gesture.Win) modifiers |= MOD_WIN;

        if (!RegisterHotKey(_hwnd, id, modifiers, (uint)vk.Value)) return false;

        _ids.Add(id);
        return true;
    }

    public void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _ids)
            {
                try { UnregisterHotKey(_hwnd, id); } catch { }
            }
        }
        _ids.Clear();
    }

    private void MessageLoop()
    {
        _wndProc = WndProcImpl;
        var hInstance = GetModuleHandleW(null);
        _className = "SongRequestHotkeys_" + Guid.NewGuid().ToString("N");

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassExW(ref windowClass);

        _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_HOTKEY:
                HotkeyPressed?.Invoke(this, (int)wParam);
                return IntPtr.Zero;
            case WM_CLOSE:
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        try { UnregisterAll(); } catch { }
        try { if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
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
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}

/// <summary>WPF-style key name → Windows virtual-key code.</summary>
internal static class WindowsVirtualKeys
{
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Return"] = 0x0D, ["Back"] = 0x08, ["Tab"] = 0x09, ["Escape"] = 0x1B,
        ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["OemOpenBrackets"] = 0xDB, ["OemCloseBrackets"] = 0xDD, ["OemSemicolon"] = 0xBA, ["OemQuotes"] = 0xDE,
        ["OemComma"] = 0xBC, ["OemPeriod"] = 0xBE, ["OemQuestion"] = 0xBF, ["OemPipe"] = 0xDC, ["OemTilde"] = 0xC0,
        ["Minus"] = 0xBD, ["Plus"] = 0xBB, ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["Scroll"] = 0x91,
        ["Snapshot"] = 0x2C, ["Pause"] = 0x13, ["Apps"] = 0x5D,
        ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Multiply"] = 0x6A, ["Divide"] = 0x6F, ["Decimal"] = 0x6E,
    };

    public static int? ToVirtualKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        key = HotkeyGesture.NormalizeKey(key);

        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            return char.ToUpperInvariant(key[0]);

        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]))
            return key[1];

        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            return 0x70 + (fn - 1);

        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6]))
            return 0x60 + (key[6] - '0');

        return Named.TryGetValue(key, out var vk) ? vk : null;
    }
}
