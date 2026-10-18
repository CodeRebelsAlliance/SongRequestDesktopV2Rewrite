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

/// <summary>
/// Creates the platform tray icon.
///
/// The host registers the platform implementation (Windows <c>Shell_NotifyIcon</c>,
/// Linux AppIndicator/StatusNotifierItem) through <see cref="BackendFactory"/>;
/// without one — macOS, mobile — <see cref="IsSupported"/> is false and the tray
/// options are hidden/disabled in the settings UI.
/// </summary>
public static class TrayIconFactory
{
    /// <summary>Whether this host has a tray. Registered by the host.</summary>
    public static Func<bool>? IsSupportedProvider { get; set; }

    /// <summary>Creates the platform tray icon. Registered by the host.</summary>
    public static Func<string, string?, ITrayIcon>? BackendFactory { get; set; }

    public static bool IsSupported => IsSupportedProvider?.Invoke() ?? false;

    public static ITrayIcon Create(string tooltip, string? iconPath = null)
        => BackendFactory?.Invoke(tooltip, iconPath) ?? new NullTrayIcon();
}
