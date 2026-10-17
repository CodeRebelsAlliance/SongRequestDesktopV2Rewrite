using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Linux system-tray icon implemented with AppIndicator
/// (StatusNotifierItem) via <see cref="AppIndicatorNative"/>.
///
/// Must be constructed on the GTK main thread (after GTK has been initialised by
/// the Photino window); the factory guards any failure and falls back to
/// <see cref="NullTrayIcon"/>. Menu activation callbacks fire on the GTK thread.
/// </summary>
internal sealed class LinuxTrayIcon : ITrayIcon
{
    private readonly List<Delegate> _callbacks = new(); // keep native callbacks alive
    private readonly List<IntPtr> _items = new();
    private IntPtr _indicator;
    private IntPtr _menu;
    private bool _disposed;

    public bool IsSupported => _indicator != IntPtr.Zero;

    public event EventHandler<TrayCommand>? CommandInvoked;

    /// <param name="iconPath">
    /// Absolute path to a PNG/icon to use; when missing a themed fallback name is
    /// used. AppIndicator accepts absolute paths for the icon.
    /// </param>
    public LinuxTrayIcon(string tooltip, string? iconPath = null)
    {
        if (!AppIndicatorNative.TryLoad(out var error))
            throw new InvalidOperationException(error);

        var native = AppIndicatorNative.AppIndicatorNew!;
        var gtkMenuNew = AppIndicatorNative.GtkMenuNew!;
        var gtkMenuItemNewWithLabel = AppIndicatorNative.GtkMenuItemNewWithLabel!;
        var gtkMenuShellAppend = AppIndicatorNative.GtkMenuShellAppend!;
        var gtkWidgetShowAll = AppIndicatorNative.GtkWidgetShowAll!;
        var gSignalConnect = AppIndicatorNative.GSignalConnectData!;

        _menu = gtkMenuNew();
        if (_menu == IntPtr.Zero)
            throw new InvalidOperationException("gtk_menu_new failed (is a display available?).");

        foreach (var item in TrayMenu.Build())
        {
            var widget = gtkMenuItemNewWithLabel(item.Label);
            if (widget == IntPtr.Zero) continue;
            _items.Add(widget);

            // "activate" signal handler: void (GtkMenuItem*, gpointer)
            GtkMenuItemActivate handler = (_, _) => CommandInvoked?.Invoke(this, item.Command);
            _callbacks.Add(handler);
            gSignalConnect(widget, "activate", Marshal.GetFunctionPointerForDelegate(handler), IntPtr.Zero, IntPtr.Zero, 0);

            gtkMenuShellAppend(_menu, widget);
        }

        gtkWidgetShowAll(_menu);

        var icon = !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)
            ? iconPath!
            : "applications-multimedia";

        _indicator = native("songrequest", icon, AppIndicatorNative.CategoryApplicationStatus);
        if (_indicator == IntPtr.Zero)
            throw new InvalidOperationException("app_indicator_new failed.");

        var setMenu = AppIndicatorNative.AppIndicatorSetMenu!;
        setMenu(_indicator, _menu);

        var setIcon = AppIndicatorNative.AppIndicatorSetIconFull!;
        setIcon(_indicator, icon, string.IsNullOrWhiteSpace(tooltip) ? "SongRequest V2" : tooltip);
    }

    public void Show()
    {
        if (_indicator == IntPtr.Zero) return;
        try { AppIndicatorNative.AppIndicatorSetStatus!(_indicator, AppIndicatorNative.StatusActive); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { if (_indicator != IntPtr.Zero) AppIndicatorNative.GObjectUnref!(_indicator); } catch { }
        try { if (_menu != IntPtr.Zero) AppIndicatorNative.GObjectUnref!(_menu); } catch { }
        _indicator = IntPtr.Zero;
        _menu = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GtkMenuItemActivate(IntPtr menuItem, IntPtr userData);
}
