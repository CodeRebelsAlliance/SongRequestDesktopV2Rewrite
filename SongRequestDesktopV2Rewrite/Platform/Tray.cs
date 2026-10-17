using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Actions a tray icon can trigger.</summary>
public enum TrayCommand
{
    Show,
    TogglePlayPause,
    Next,
    Stop,
    Exit,
}

public sealed record TrayMenuItem(TrayCommand Command, string Label);

/// <summary>The tray context-menu definition (pure, unit-tested).</summary>
public static class TrayMenu
{
    public static IReadOnlyList<TrayMenuItem> Build() => new List<TrayMenuItem>
    {
        new(TrayCommand.Show, "Show SongRequest"),
        new(TrayCommand.TogglePlayPause, "Play / Pause"),
        new(TrayCommand.Next, "Next track"),
        new(TrayCommand.Stop, "Stop"),
        new(TrayCommand.Exit, "Quit"),
    };
}

/// <summary>A system-tray icon.</summary>
public interface ITrayIcon : IDisposable
{
    /// <summary>Whether this platform actually has a tray icon.</summary>
    bool IsSupported { get; }

    void Show();

    event EventHandler<TrayCommand>? CommandInvoked;
}

/// <summary>No-op tray used on platforms without tray support.</summary>
public sealed class NullTrayIcon : ITrayIcon
{
    public bool IsSupported => false;

    public event EventHandler<TrayCommand>? CommandInvoked;

    public void Show() { }

    /// <summary>Raises <see cref="CommandInvoked"/> (used by tests).</summary>
    internal void Raise(TrayCommand command) => CommandInvoked?.Invoke(this, command);

    public void Dispose() { }
}

/// <summary>Creates the platform tray icon: Windows (Shell_NotifyIcon) or Linux (AppIndicator).</summary>
public static class TrayIconFactory
{
    public static bool IsSupported =>
        OperatingSystem.IsWindows() ||
        (OperatingSystem.IsLinux() && AppIndicatorNative.IsAvailable);

    public static ITrayIcon Create(string tooltip, string? iconPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new Win32TrayIcon(tooltip); }
            catch { return new NullTrayIcon(); }
        }

        if (OperatingSystem.IsLinux())
        {
            // Requires GTK to be initialised (the Photino window does that) and a
            // StatusNotifier host; any failure falls back to no tray.
            try { return new LinuxTrayIcon(tooltip, iconPath); }
            catch { return new NullTrayIcon(); }
        }

        return new NullTrayIcon();
    }
}
