using System;
using System.Collections.Generic;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

/// <summary>Registers global (OS-wide) hotkeys. Implemented per platform.</summary>
internal interface IGlobalHotkeyBackend : IDisposable
{
    bool IsSupported { get; }

    /// <summary>Raised (on an arbitrary thread) with the registration id when a hotkey fires.</summary>
    event EventHandler<int>? HotkeyPressed;

    /// <summary>Registers a gesture. Returns false when the OS refuses (e.g. already taken).</summary>
    bool Register(int id, HotkeyGesture gesture);

    void UnregisterAll();
}

/// <summary>No-op backend for platforms without global-hotkey support.</summary>
internal sealed class NullGlobalHotkeyBackend : IGlobalHotkeyBackend
{
    public bool IsSupported => false;

    public event EventHandler<int>? HotkeyPressed;

    public bool Register(int id, HotkeyGesture gesture) => false;

    public void UnregisterAll() { }

    internal void Raise(int id) => HotkeyPressed?.Invoke(this, id);

    public void Dispose() { }
}

/// <summary>
/// Registers configured global hotkeys and raises <see cref="ActionTriggered"/>
/// with the web UI's action id. Platform access is behind
/// <see cref="IGlobalHotkeyBackend"/> so the orchestration is unit-testable.
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private readonly IGlobalHotkeyBackend _backend;
    private readonly Dictionary<int, string> _byId = new();
    private int _nextId = 1;
    private bool _disposed;

    public GlobalHotkeyService(IGlobalHotkeyBackend backend)
    {
        _backend = backend;
        _backend.HotkeyPressed += OnHotkeyPressed;
    }

    public bool IsSupported => _backend.IsSupported;

    /// <summary>Number of hotkeys currently registered.</summary>
    public int RegisteredCount => _byId.Count;

    /// <summary>Raised with the action id (e.g. <c>PlayPause</c>) when a hotkey fires.</summary>
    public event EventHandler<string>? ActionTriggered;

    /// <summary>Registers every enabled, parseable gesture. Returns how many were registered.</summary>
    public int RegisterAll(IEnumerable<(string ActionId, string Gesture)> definitions)
    {
        Clear();
        if (!_backend.IsSupported) return 0;

        int count = 0;
        foreach (var (actionId, gesture) in definitions)
        {
            if (string.IsNullOrWhiteSpace(actionId)) continue;
            if (!HotkeyGesture.TryParse(gesture, out var parsed)) continue;

            int id = _nextId++;
            if (_backend.Register(id, parsed))
            {
                _byId[id] = actionId;
                count++;
            }
        }

        return count;
    }

    public void Clear()
    {
        _backend.UnregisterAll();
        _byId.Clear();
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (_byId.TryGetValue(id, out var actionId))
            ActionTriggered?.Invoke(this, actionId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.HotkeyPressed -= OnHotkeyPressed;
        try { _backend.Dispose(); } catch { }
    }
}

/// <summary>
/// Creates the platform global-hotkey backend (cached).
///
/// The host registers the platform backend through <see cref="BackendFactory"/>
/// (Windows <c>RegisterHotKey</c>, Linux/X11 <c>XGrabKey</c>); without one — macOS,
/// Wayland, mobile — a no-op backend is used and the settings toggle is disabled.
/// </summary>
internal static class GlobalHotkeyBackendFactory
{
    private static readonly object Sync = new();
    private static IGlobalHotkeyBackend? _cached;

    /// <summary>Platform backend, registered by the host.</summary>
    internal static Func<IGlobalHotkeyBackend>? BackendFactory { get; set; }

    public static bool IsSupported => Get().IsSupported;

    public static IGlobalHotkeyBackend Create() => Get();

    private static IGlobalHotkeyBackend Get()
    {
        if (_cached != null) return _cached;

        lock (Sync)
        {
            if (_cached != null) return _cached;

            IGlobalHotkeyBackend backend = new NullGlobalHotkeyBackend();
            var factory = BackendFactory;
            if (factory != null)
            {
                try { backend = factory(); } catch { backend = new NullGlobalHotkeyBackend(); }
            }

            _cached = backend;
            return backend;
        }
    }
}
