using System;
using System.Runtime.InteropServices;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Native bindings for the Linux system tray via AppIndicator
/// (StatusNotifierItem). Loads whichever library is present
/// (<c>libayatana-appindicator3</c> or the older <c>libappindicator3</c>) plus
/// GTK 3 / GObject, resolving symbols lazily with <see cref="NativeLibrary"/>.
///
/// <see cref="IsAvailable"/> only loads libraries and resolves symbols, so it is
/// safe to call before GTK is initialised (no display required). Constructing an
/// indicator/menu does require a GTK display.
/// </summary>
internal static class AppIndicatorNative
{
    private static readonly object Sync = new();
    private static bool _attempted;
    private static bool _available;

    private static IntPtr _appIndicatorLibrary;
    private static IntPtr _gtkLibrary;
    private static IntPtr _gobjectLibrary;

    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _available;
        }
    }

    // Application status category / active status.
    public const int CategoryApplicationStatus = 0;
    public const int StatusActive = 1;

    // ── Resolved functions ─────────────────────────────────────────────────

    public static AppIndicatorNewDelegate? AppIndicatorNew { get; private set; }
    public static AppIndicatorSetStatusDelegate? AppIndicatorSetStatus { get; private set; }
    public static AppIndicatorSetMenuDelegate? AppIndicatorSetMenu { get; private set; }
    public static AppIndicatorSetIconFullDelegate? AppIndicatorSetIconFull { get; private set; }

    public static GtkMenuNewDelegate? GtkMenuNew { get; private set; }
    public static GtkMenuItemNewWithLabelDelegate? GtkMenuItemNewWithLabel { get; private set; }
    public static GtkMenuShellAppendDelegate? GtkMenuShellAppend { get; private set; }
    public static GtkWidgetShowAllDelegate? GtkWidgetShowAll { get; private set; }
    public static GSignalConnectDataDelegate? GSignalConnectData { get; private set; }
    public static GObjectUnrefDelegate? GObjectUnref { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr AppIndicatorNewDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
        int category);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AppIndicatorSetStatusDelegate(IntPtr self, int status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AppIndicatorSetMenuDelegate(IntPtr self, IntPtr menu);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AppIndicatorSetIconFullDelegate(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconDesc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GtkMenuNewDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GtkMenuItemNewWithLabelDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string label);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GtkMenuShellAppendDelegate(IntPtr menuShell, IntPtr child);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GtkWidgetShowAllDelegate(IntPtr widget);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong GSignalConnectDataDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string detailedSignal,
        IntPtr handler,
        IntPtr data,
        IntPtr destroyData,
        int connectFlags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GObjectUnrefDelegate(IntPtr obj);

    // ── Loading ────────────────────────────────────────────────────────────

    public static bool TryLoad(out string? error)
    {
        EnsureLoaded();
        error = _available ? null : "AppIndicator (libayatana-appindicator3 / libappindicator3) is not available.";
        return _available;
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_attempted) return;
            _attempted = true;

            try
            {
                foreach (var name in new[]
                {
                    "libayatana-appindicator3.so.1",
                    "libayatana-appindicator3.so",
                    "libappindicator3.so.1",
                    "libappindicator3.so",
                })
                {
                    if (NativeLibrary.TryLoad(name, out var handle))
                    {
                        _appIndicatorLibrary = handle;
                        break;
                    }
                }

                if (_appIndicatorLibrary == IntPtr.Zero) return;
                if (!NativeLibrary.TryLoad("libgtk-3.so.0", out _gtkLibrary)) return;
                if (!NativeLibrary.TryLoad("libgobject-2.0.so.0", out _gobjectLibrary)) return;

                AppIndicatorNew = Resolve<AppIndicatorNewDelegate>(_appIndicatorLibrary, "app_indicator_new");
                AppIndicatorSetStatus = Resolve<AppIndicatorSetStatusDelegate>(_appIndicatorLibrary, "app_indicator_set_status");
                AppIndicatorSetMenu = Resolve<AppIndicatorSetMenuDelegate>(_appIndicatorLibrary, "app_indicator_set_menu");
                AppIndicatorSetIconFull = Resolve<AppIndicatorSetIconFullDelegate>(_appIndicatorLibrary, "app_indicator_set_icon_full");

                GtkMenuNew = Resolve<GtkMenuNewDelegate>(_gtkLibrary, "gtk_menu_new");
                GtkMenuItemNewWithLabel = Resolve<GtkMenuItemNewWithLabelDelegate>(_gtkLibrary, "gtk_menu_item_new_with_label");
                GtkMenuShellAppend = Resolve<GtkMenuShellAppendDelegate>(_gtkLibrary, "gtk_menu_shell_append");
                GtkWidgetShowAll = Resolve<GtkWidgetShowAllDelegate>(_gtkLibrary, "gtk_widget_show_all");

                GSignalConnectData = Resolve<GSignalConnectDataDelegate>(_gobjectLibrary, "g_signal_connect_data");
                GObjectUnref = Resolve<GObjectUnrefDelegate>(_gobjectLibrary, "g_object_unref");

                _available = true;
            }
            catch
            {
                _available = false;
            }
        }
    }

    private static T Resolve<T>(IntPtr library, string symbol) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
}
