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

/// <summary>One playlist row exposed to the car media browser.</summary>
public sealed record CarPlaylistEntry(
    string Id,
    string Name,
    string Icon,
    int SongCount);

/// <summary>
/// Bridge between the Android Auto MediaBrowserService and the running app.
/// AppHost registers the browse/search/play callbacks once the library and
/// engine are available; the service degrades to an empty library if not.
/// </summary>
public static class CarMediaBridge
{
    public static Func<IReadOnlyList<CarPlaylistEntry>>? BrowsePlaylists { get; private set; }
    public static Func<string, IReadOnlyList<CarMediaEntry>>? BrowsePlaylistSongs { get; private set; }
    public static Func<string, string, bool>? PlayPlaylist { get; private set; }

    public static Func<IReadOnlyList<CarMediaEntry>>? BrowseLibrary { get; private set; }
    public static Func<IReadOnlyList<CarMediaEntry>>? BrowseQueue { get; private set; }
    public static Func<string, IReadOnlyList<CarMediaEntry>>? Search { get; private set; }
    public static Func<string, bool>? Play { get; private set; }

    /// <summary>True once the phone-side startup flow has completed.</summary>
    public static bool IsReady { get; private set; }

    public static event Action? ReadyChanged;

    /// <summary>Raised when the play queue or playlist queue changes.</summary>
    public static event Action? QueueChanged;

    public static void SetReady(bool ready)
    {
        if (IsReady == ready) return;
        IsReady = ready;
        try { ReadyChanged?.Invoke(); } catch { }
    }

    public static void NotifyQueueChanged()
    {
        try { QueueChanged?.Invoke(); } catch { }
    }

    public static bool IsAvailable => BrowseLibrary != null && Play != null;

    public static void Register(
        Func<IReadOnlyList<CarPlaylistEntry>> browsePlaylists,
        Func<string, IReadOnlyList<CarMediaEntry>> browsePlaylistSongs,
        Func<string, string, bool> playPlaylist,
        Func<IReadOnlyList<CarMediaEntry>> browseLibrary,
        Func<IReadOnlyList<CarMediaEntry>> browseQueue,
        Func<string, IReadOnlyList<CarMediaEntry>> search,
        Func<string, bool> play)
    {
        BrowsePlaylists = browsePlaylists;
        BrowsePlaylistSongs = browsePlaylistSongs;
        PlayPlaylist = playPlaylist;
        BrowseLibrary = browseLibrary;
        BrowseQueue = browseQueue;
        Search = search;
        Play = play;
    }

    public static IReadOnlyList<CarPlaylistEntry> Playlists()
    {
        try { return BrowsePlaylists?.Invoke() ?? Array.Empty<CarPlaylistEntry>(); }
        catch { return Array.Empty<CarPlaylistEntry>(); }
    }

    public static IReadOnlyList<CarMediaEntry> PlaylistSongs(string playlistId)
    {
        try { return BrowsePlaylistSongs?.Invoke(playlistId) ?? Array.Empty<CarMediaEntry>(); }
        catch { return Array.Empty<CarMediaEntry>(); }
    }

    public static bool PlayPlaylistFrom(string playlistId, string songId)
    {
        try { return PlayPlaylist?.Invoke(playlistId, songId) == true; }
        catch { return false; }
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
