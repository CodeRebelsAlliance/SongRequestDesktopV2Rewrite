using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Linux global hotkeys via X11 <c>XGrabKey</c>. Works on **X11 only** — Wayland
/// does not permit global key grabs, so <see cref="GlobalHotkeyBackendFactory"/>
/// falls back to no global hotkeys there. NOTE: not runnable on headless CI.
/// </summary>
internal sealed class X11GlobalHotkeyBackend : IGlobalHotkeyBackend
{
    private const int XKeyPress = 2;
    private const long KeyPressMask = 1L;
    private const int GrabModeAsync = 1;
    private const int False = 0;

    private const uint ShiftMask = 1u << 0;
    private const uint LockMask = 1u << 1;
    private const uint ControlMask = 1u << 2;
    private const uint Mod1Mask = 1u << 3; // Alt
    private const uint Mod2Mask = 1u << 4; // NumLock
    private const uint Mod4Mask = 1u << 6; // Super/Win

    private const int EventBufferSize = 192; // sizeof(XEvent) is 192 on 64-bit Xlib

    private readonly IntPtr _display;
    private readonly ulong _root;
    private readonly IntPtr _eventBuffer;
    private readonly Thread _thread;
    private readonly Dictionary<(int keycode, uint state), int> _bindings = new();
    private readonly List<(int keycode, uint state)> _grabs = new();
    private volatile bool _running = true;

    public bool IsSupported => _display != IntPtr.Zero;

    public event EventHandler<int>? HotkeyPressed;

    public X11GlobalHotkeyBackend()
    {
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Could not open an X display (Wayland is not supported).");

        _root = XDefaultRootWindow(_display);
        XSelectInput(_display, _root, KeyPressMask);
        _eventBuffer = Marshal.AllocHGlobal(EventBufferSize);

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "songrequest-hotkeys" };
        _thread.Start();
    }

    public bool Register(int id, HotkeyGesture gesture)
    {
        var keysymName = X11KeysymNames.ToKeysymName(gesture.Key);
        if (keysymName == null) return false;

        var keysym = XStringToKeysym(keysymName);
        if (keysym == IntPtr.Zero) return false;

        int keycode = XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return false;

        uint state = 0;
        if (gesture.Ctrl) state |= ControlMask;
        if (gesture.Alt) state |= Mod1Mask;
        if (gesture.Shift) state |= ShiftMask;
        if (gesture.Win) state |= Mod4Mask;

        // Grab every combination of CapsLock/NumLock so the hotkey still fires.
        foreach (var ignored in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
        {
            uint mods = state | ignored;
            XGrabKey(_display, keycode, mods, _root, False, GrabModeAsync, GrabModeAsync);
            _grabs.Add((keycode, mods));
            _bindings[(keycode, mods)] = id;
        }

        XFlush(_display);
        return true;
    }

    public void UnregisterAll()
    {
        if (_display == IntPtr.Zero) { _grabs.Clear(); _bindings.Clear(); return; }

        foreach (var (keycode, state) in _grabs)
        {
            try { XUngrabKey(_display, keycode, state, _root); } catch { }
        }

        _grabs.Clear();
        _bindings.Clear();
        try { XFlush(_display); } catch { }
    }

    private void EventLoop()
    {
        while (_running)
        {
            int result;
            try { result = XNextEvent(_display, _eventBuffer); }
            catch { break; }
            if (result != 0) break;

            var evt = Marshal.PtrToStructure<XKeyEvent>(_eventBuffer);
            if (evt.type != XKeyPress) continue;

            uint state = evt.state & ~(LockMask | Mod2Mask);
            if (_bindings.TryGetValue(((int)evt.keycode, state), out var id))
                HotkeyPressed?.Invoke(this, id);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { UnregisterAll(); } catch { }

        // The event thread may be blocked in XNextEvent; it is a background thread,
        // so the X display is intentionally not closed here (avoids a use-after-free
        // at shutdown). The connection is released when the process exits.
    }

    // ── Interop ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public ulong window;
        public ulong root;
        public ulong subwindow;
        public ulong time;
        public int x;
        public int y;
        public int x_root;
        public int y_root;
        public uint state;
        public uint keycode;
        public int same_screen;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern ulong XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSelectInput(IntPtr display, ulong window, long eventMask);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XStringToKeysym(string keysymName);

    [DllImport("libX11.so.6")]
    private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [DllImport("libX11.so.6")]
    private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, ulong grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

    [DllImport("libX11.so.6")]
    private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, ulong grabWindow);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);
}

/// <summary>WPF-style key name → X11 keysym name.</summary>
internal static class X11KeysymNames
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "space", ["Return"] = "Return", ["Back"] = "BackSpace", ["Tab"] = "Tab", ["Escape"] = "Escape",
        ["Delete"] = "Delete", ["Insert"] = "Insert", ["Home"] = "Home", ["End"] = "End",
        ["PageUp"] = "Prior", ["PageDown"] = "Next", ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right",
        ["OemOpenBrackets"] = "bracketleft", ["OemCloseBrackets"] = "bracketright", ["OemSemicolon"] = "semicolon",
        ["OemQuotes"] = "apostrophe", ["OemComma"] = "comma", ["OemPeriod"] = "period", ["OemQuestion"] = "slash",
        ["OemPipe"] = "backslash", ["OemTilde"] = "grave", ["Minus"] = "minus", ["Plus"] = "equal",
        ["CapsLock"] = "Caps_Lock", ["NumLock"] = "Num_Lock", ["Scroll"] = "Scroll_Lock",
        ["Snapshot"] = "Print", ["Pause"] = "Pause", ["Apps"] = "Menu",
        ["Add"] = "KP_Add", ["Subtract"] = "KP_Subtract", ["Multiply"] = "KP_Multiply", ["Divide"] = "KP_Divide", ["Decimal"] = "KP_Decimal",
    };

    public static string? ToKeysymName(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        key = HotkeyGesture.NormalizeKey(key);

        if (key.Length == 1 && char.IsLetter(key[0])) return key.ToLowerInvariant();
        if (key.Length == 1 && char.IsDigit(key[0])) return key;

        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return key[1].ToString();

        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            return "F" + fn;

        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsDigit(key[6]))
            return "KP_" + key[6];

        return Named.TryGetValue(key, out var name) ? name : null;
    }
}
