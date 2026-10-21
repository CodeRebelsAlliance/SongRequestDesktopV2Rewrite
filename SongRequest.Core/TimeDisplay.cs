using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Shared display formatting for durations and queue wait times. TimeSpan's
/// <c>mm</c> format shows the minute *component*, so a 1h05 track printed as
/// "05:00"; these helpers keep the hours instead.
/// </summary>
public static class TimeDisplay
{
    /// <summary><c>mm:ss</c> below an hour, <c>h:mm:ss</c> from an hour onwards.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;

        var totalSeconds = (long)Math.Floor(value.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes:D2}:{seconds:D2}";
    }

    /// <summary><c>mm:ss</c> below an hour, <c>h:mm:ss</c> from an hour onwards.</summary>
    public static string Clock(double totalSeconds)
        => Clock(TimeSpan.FromSeconds(Math.Max(0, totalSeconds)));
}
