using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequest.Mobile;

/// <summary>One item exposed to the car media browser.</summary>
public sealed record CarMediaEntry(
    string Id,
    string Title,
    string Artist,
    string Album,
    string? ArtworkPath,
    long DurationMilliseconds);

/// <summary>
/// Bridge between the Android Auto MediaBrowserService and the running app.
/// AppHost registers the browse/search/play callbacks once the library and
/// engine are available; the service degrades to an empty library if not.
/// </summary>
public static class CarMediaBridge
{
    public static Func<IReadOnlyList<CarMediaEntry>>? BrowseLibrary { get; private set; }
    public static Func<IReadOnlyList<CarMediaEntry>>? BrowseQueue { get; private set; }
    public static Func<string, IReadOnlyList<CarMediaEntry>>? Search { get; private set; }
    public static Func<string, bool>? Play { get; private set; }

    /// <summary>True once the phone-side startup flow has completed.</summary>
    public static bool IsReady { get; private set; }

    public static event Action? ReadyChanged;

    public static void SetReady(bool ready)
    {
        if (IsReady == ready) return;
        IsReady = ready;
        try { ReadyChanged?.Invoke(); } catch { }
    }

    public static bool IsAvailable => BrowseLibrary != null && Play != null;

    public static void Register(
        Func<IReadOnlyList<CarMediaEntry>> browseLibrary,
        Func<IReadOnlyList<CarMediaEntry>> browseQueue,
        Func<string, IReadOnlyList<CarMediaEntry>> search,
        Func<string, bool> play)
    {
        BrowseLibrary = browseLibrary;
        BrowseQueue = browseQueue;
        Search = search;
        Play = play;
    }

    public static IReadOnlyList<CarMediaEntry> Library()
    {
        try { return BrowseLibrary?.Invoke() ?? Array.Empty<CarMediaEntry>(); }
        catch { return Array.Empty<CarMediaEntry>(); }
    }

    public static IReadOnlyList<CarMediaEntry> Queue()
    {
        try { return BrowseQueue?.Invoke() ?? Array.Empty<CarMediaEntry>(); }
        catch { return Array.Empty<CarMediaEntry>(); }
    }

    public static IReadOnlyList<CarMediaEntry> SearchFor(string query)
    {
        try { return Search?.Invoke(query) ?? Array.Empty<CarMediaEntry>(); }
        catch { return Array.Empty<CarMediaEntry>(); }
    }

    public static bool PlayMediaId(string mediaId)
    {
        try { return Play?.Invoke(mediaId) == true; }
        catch { return false; }
    }
}
