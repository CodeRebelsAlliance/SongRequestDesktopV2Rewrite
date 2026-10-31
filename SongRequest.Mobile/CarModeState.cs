using System;

namespace SongRequest.Mobile;

/// <summary>
/// Cross-platform flag set by the Android host when the device is in car UI
/// mode and/or an Android Auto media client is connected. The shell uses it to
/// choose the car-optimized music-only layout.
/// </summary>
public static class CarModeState
{
    private static bool _uiModeCar;
    private static bool _androidAutoConnected;

    public static bool IsCarMode => _uiModeCar || _androidAutoConnected;

    public static bool IsUiModeCar => _uiModeCar;
    public static bool IsAndroidAutoConnected => _androidAutoConnected;

    public static event Action<bool>? Changed;

    /// <summary>Set from MainActivity's configuration/UiMode detection.</summary>
    public static void SetUiMode(bool enabled) => Update(() => _uiModeCar = enabled);

    /// <summary>Set while the Android Auto MediaBrowserService is connected.</summary>
    public static void SetAndroidAutoConnected(bool connected)
        => Update(() => _androidAutoConnected = connected);

    private static void Update(Action change)
    {
        bool before = IsCarMode;
        change();
        if (before == IsCarMode) return;
        try { Changed?.Invoke(IsCarMode); } catch { }
    }
}
