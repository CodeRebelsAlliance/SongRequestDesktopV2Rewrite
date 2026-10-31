using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using MediaBrowserCompat = Android.Support.V4.Media.MediaBrowserCompat;
using MediaDescriptionCompat = Android.Support.V4.Media.MediaDescriptionCompat;
using MediaSessionCompat = Android.Support.V4.Media.Session.MediaSessionCompat;
using MediaBrowserServiceCompat = AndroidX.Media.MediaBrowserServiceCompat;

namespace SongRequest.Mobile;

/// <summary>
/// Android Auto media-browser entry point. Android Auto discovers this service
/// from the manifest, browses playlists/library/queue, searches the library, and
/// uses the shared MediaSession for playback and transport controls.
///
/// Playlists are the first (default) destination. The browse tree stays behind a
/// single waiting item until the phone-side startup flow has completed; the car
/// never runs the app's loading sequence itself.
/// </summary>
[Service(
    Name = "net.bycra.songrequest.CarMediaService",
    Exported = true,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
public sealed class CarMediaService : MediaBrowserServiceCompat
{
    public const string RootId = "root";
    public const string PlaylistsId = "folder:playlists";
    public const string LibraryId = "folder:library";
    public const string QueueId = "folder:queue";
    public const string WaitingId = "waiting";

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidSessionHolder.SessionChanged += UpdateSessionToken;
        UpdateSessionToken();

        CarMediaBridge.ReadyChanged += OnReadyChanged;
        CarMediaBridge.QueueChanged += OnQueueChanged;
    }

    public override void OnDestroy()
    {
        AndroidSessionHolder.SessionChanged -= UpdateSessionToken;
        CarMediaBridge.ReadyChanged -= OnReadyChanged;
        CarMediaBridge.QueueChanged -= OnQueueChanged;
        CarModeState.SetAndroidAutoConnected(false);
        try { AppHost.Current?.ApplyCarMode(); } catch { }
        base.OnDestroy();
    }

    private void UpdateSessionToken()
    {
        try
        {
            var token = AndroidSessionHolder.Current;
            if (token != null)
                SessionToken = MediaSessionCompat.Token.FromToken(token)!;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Car media session token failed: {ex.Message}");
        }
    }

    private void OnReadyChanged()
    {
        try
        {
            NotifyChildrenChanged(RootId);
            NotifyChildrenChanged(PlaylistsId);
            NotifyChildrenChanged(LibraryId);
            NotifyChildrenChanged(QueueId);
        }
        catch { }
    }

    private void OnQueueChanged()
    {
        try { NotifyChildrenChanged(QueueId); } catch { }
    }

    public override BrowserRoot? OnGetRoot(string? clientPackageName, int clientUid, Bundle? rootHints)
    {
        // Binding from Android Auto / an automotive media client is the connection
        // signal that must switch the phone to the music-only operating mode.
        if (IsCarClient(clientPackageName) || CarModeState.IsUiModeCar)
        {
            CarModeState.SetAndroidAutoConnected(true);
            try { AppHost.Current?.ApplyCarMode(); } catch { }
        }

        return new BrowserRoot(RootId, null);
    }

    private static bool IsCarClient(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return false;
        var name = packageName.ToLowerInvariant();
        return name.Contains("projection.gearhead") ||
               name.Contains("androidauto") ||
               name.Contains("automotive") ||
               name.Contains("com.android.car") ||
               name.Contains("car.media");
    }

    public override void OnLoadChildren(string? parentId, MediaBrowserServiceCompat.Result? result)
    {
        if (result == null) return;

        var items = new List<MediaBrowserCompat.MediaItem>();
        try
        {
            if (!CarMediaBridge.IsReady)
            {
                // The phone owns the startup/loading flow. The car surface only
                // shows this waiting entry until that flow completes.
                items.Add(Folder(WaitingId, "Waiting for SongRequest…",
                    "Loading on your phone"));
                Send(result, items);
                return;
            }

            if (string.IsNullOrEmpty(parentId) || parentId == RootId)
            {
                // Playlists first: that is the default car destination.
                items.Add(Folder(PlaylistsId, "Playlists", "Your playlists"));
                items.Add(Folder(LibraryId, "Library", "All songs"));
                items.Add(Folder(QueueId, "Queue", "Up next"));
            }
            else if (parentId == PlaylistsId)
            {
                items.AddRange(CarMediaBridge.Playlists().Select(ToPlaylistItem));
            }
            else if (parentId.StartsWith("playlist:", StringComparison.Ordinal))
            {
                var playlistId = parentId["playlist:".Length..];
                items.AddRange(CarMediaBridge.PlaylistSongs(playlistId)
                    .Select(entry => ToMediaItem(entry,
                        $"plsong:{playlistId}:{SongIdOf(entry)}")));
            }
            else if (parentId == LibraryId)
            {
                items.AddRange(CarMediaBridge.Library().Select(ToMediaItem));
            }
            else if (parentId == QueueId)
            {
                items.AddRange(CarMediaBridge.Queue().Select(ToMediaItem));
            }
            else if (parentId == WaitingId)
            {
                // No children while the phone is still loading.
            }
        }
        catch { }

        Send(result, items);
    }

    public override void OnSearch(string? query, Bundle? extras, MediaBrowserServiceCompat.Result? result)
    {
        if (result == null) return;

        var items = new List<MediaBrowserCompat.MediaItem>();
        try
        {
            if (CarMediaBridge.IsReady && !string.IsNullOrWhiteSpace(query))
            {
                // Songs first, then playlists with matching names.
                items.AddRange(CarMediaBridge.SearchFor(query).Select(ToMediaItem));
                items.AddRange(CarMediaBridge.Playlists()
                    .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(ToPlaylistItem));
            }
        }
        catch { }

        Send(result, items);
    }

    private static void Send(MediaBrowserServiceCompat.Result result, List<MediaBrowserCompat.MediaItem> items)
    {
        try { result.SendResult(new JavaList<MediaBrowserCompat.MediaItem>(items)); }
        catch { try { result.SendResult(null); } catch { } }
    }

    private static MediaBrowserCompat.MediaItem Folder(string id, string title, string subtitle)
    {
        var description = new MediaDescriptionCompat.Builder()
            .SetMediaId(id)
            .SetTitle(new Java.Lang.String(title))
            .SetSubtitle(new Java.Lang.String(subtitle))
            .Build();
        return new MediaBrowserCompat.MediaItem(description,
            MediaBrowserCompat.MediaItem.FlagBrowsable);
    }

    private static MediaBrowserCompat.MediaItem ToPlaylistItem(CarPlaylistEntry playlist)
    {
        var description = new MediaDescriptionCompat.Builder()
            .SetMediaId("playlist:" + playlist.Id)
            .SetTitle(new Java.Lang.String(playlist.Name))
            .SetSubtitle(new Java.Lang.String(playlist.SongCount + " songs"))
            .Build();
        return new MediaBrowserCompat.MediaItem(description,
            MediaBrowserCompat.MediaItem.FlagBrowsable);
    }

    private static MediaBrowserCompat.MediaItem ToMediaItem(CarMediaEntry entry)
        => ToMediaItem(entry, entry.Id);

    private static string SongIdOf(CarMediaEntry entry)
        => entry.Id.StartsWith("library:", StringComparison.Ordinal)
            ? entry.Id["library:".Length..]
            : entry.Id;

    private static MediaBrowserCompat.MediaItem ToMediaItem(CarMediaEntry entry, string mediaId)
    {
        var subtitle = string.IsNullOrWhiteSpace(entry.Album)
            ? entry.Artist
            : entry.Artist + " · " + entry.Album;

        var description = new MediaDescriptionCompat.Builder()
            .SetMediaId(mediaId)
            .SetTitle(new Java.Lang.String(entry.Title))
            .SetSubtitle(new Java.Lang.String(subtitle))
            .SetIconBitmap(MediaArtwork.Load(entry.ArtworkPath))
            .Build();

        return new MediaBrowserCompat.MediaItem(description,
            MediaBrowserCompat.MediaItem.FlagPlayable);
    }
}
