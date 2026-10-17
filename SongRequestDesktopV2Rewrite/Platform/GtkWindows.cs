using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Finds and destroys GTK toplevel windows by title.
///
/// On Linux, Photino's close only stops a window's message loop: the GTK widget can
/// stay on screen — visible but frozen and receiving nothing — which is what "the
/// window does not close" looks like. <c>gtk_window_list_toplevels()</c> makes it
/// possible to reach the widget without Photino's (Windows-only) <c>WindowHandle</c>
/// and destroy it for real.
/// </summary>
public static class GtkWindows
{
    /// <summary>Whether the GTK calls can be used on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    [DllImport("libgtk-3.so.0")]
    private static extern IntPtr gtk_window_list_toplevels();

    [DllImport("libgtk-3.so.0", CharSet = CharSet.Ansi)]
    private static extern IntPtr gtk_window_get_title(IntPtr window);

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_widget_destroy(IntPtr widget);

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_widget_hide(IntPtr widget);

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_widget_show_all(IntPtr widget);

    [DllImport("libgtk-3.so.0")]
    private static extern int gtk_widget_get_visible(IntPtr widget);

    [DllImport("libglib-2.0.so.0")]
    private static extern void g_list_free(IntPtr list);

    /// <summary>Titles of all toplevel windows in this process.</summary>
    public static IReadOnlyList<string> Titles()
    {
        var titles = new List<string>();
        if (!IsSupported) return titles;

        try
        {
            var list = gtk_window_list_toplevels();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (window == IntPtr.Zero) continue;

                var title = Marshal.PtrToStringUTF8(gtk_window_get_title(window));
                if (!string.IsNullOrEmpty(title)) titles.Add(title!);
            }

            if (list != IntPtr.Zero) g_list_free(list);
        }
        catch
        {
            // GTK not initialised in this process (or the library is missing).
        }

        return titles;
    }

    /// <summary>
    /// Destroys every toplevel whose title contains <paramref name="titleContains"/>.
    /// Returns how many windows were destroyed.
    /// </summary>
    public static int Destroy(string titleContains)
    {
        if (!IsSupported || string.IsNullOrEmpty(titleContains)) return 0;

        var destroyed = 0;
        try
        {
            var list = gtk_window_list_toplevels();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (window == IntPtr.Zero) continue;

                var title = Marshal.PtrToStringUTF8(gtk_window_get_title(window)) ?? string.Empty;
                if (title.IndexOf(titleContains, StringComparison.Ordinal) < 0) continue;

                gtk_widget_destroy(window);
                destroyed++;
            }

            if (list != IntPtr.Zero) g_list_free(list);
        }
        catch
        {
            // Best effort: never let window cleanup bring the app down.
        }

        return destroyed;
    }

    /// <summary>Titles with their visibility, for diagnostics: e.g. <c>SongRequest V2 (visible)</c>.</summary>
    public static IReadOnlyList<string> Describe()
    {
        var described = new List<string>();
        if (!IsSupported) return described;

        try
        {
            var list = gtk_window_list_toplevels();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (window == IntPtr.Zero) continue;

                var title = Marshal.PtrToStringUTF8(gtk_window_get_title(window)) ?? "(untitled)";
                described.Add($"{title} ({(gtk_widget_get_visible(window) != 0 ? "visible" : "hidden")})");
            }

            if (list != IntPtr.Zero) g_list_free(list);
        }
        catch { }

        return described;
    }

    /// <summary>Shows matching windows again (used when a hidden sub-window is reopened).</summary>
    public static int Show(string titleContains)
    {
        if (!IsSupported || string.IsNullOrEmpty(titleContains)) return 0;

        var shown = 0;
        try
        {
            var list = gtk_window_list_toplevels();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (window == IntPtr.Zero) continue;

                var title = Marshal.PtrToStringUTF8(gtk_window_get_title(window)) ?? string.Empty;
                if (title.IndexOf(titleContains, StringComparison.Ordinal) < 0) continue;

                gtk_widget_show_all(window);
                shown++;
            }

            if (list != IntPtr.Zero) g_list_free(list);
        }
        catch { }

        return shown;
    }

    /// <summary>Hides (without destroying) matching windows; a fallback if destroying fails.</summary>
    public static int Hide(string titleContains)
    {
        if (!IsSupported || string.IsNullOrEmpty(titleContains)) return 0;

        var hidden = 0;
        try
        {
            var list = gtk_window_list_toplevels();
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (window == IntPtr.Zero) continue;

                var title = Marshal.PtrToStringUTF8(gtk_window_get_title(window)) ?? string.Empty;
                if (title.IndexOf(titleContains, StringComparison.Ordinal) < 0) continue;

                gtk_widget_hide(window);
                hidden++;
            }

            if (list != IntPtr.Zero) g_list_free(list);
        }
        catch { }

        return hidden;
    }
}
