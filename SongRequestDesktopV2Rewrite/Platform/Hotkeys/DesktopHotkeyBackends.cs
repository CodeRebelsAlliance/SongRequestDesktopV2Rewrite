using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Desktop global-hotkey backends: <c>RegisterHotKey</c> on Windows,
/// <c>XGrabKey</c> on Linux/X11. Wayland does not allow global key grabs, so
/// Linux without X11 (or a failed grab) falls back to the no-op backend and the
/// settings toggle is disabled.
/// </summary>
internal static class DesktopHotkeyBackends
{
    public static IGlobalHotkeyBackend Create()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new WindowsGlobalHotkeyBackend(); }
            catch { return new NullGlobalHotkeyBackend(); }
        }

        if (OperatingSystem.IsLinux())
        {
            try { return new X11GlobalHotkeyBackend(); }
            catch { return new NullGlobalHotkeyBackend(); }
        }

        return new NullGlobalHotkeyBackend();
    }
}
