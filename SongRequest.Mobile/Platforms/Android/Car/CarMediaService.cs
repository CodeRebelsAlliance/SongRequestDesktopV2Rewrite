using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Media;
using Android.Media.Browse;
using Android.Runtime;
using Android.OS;
using Android.Service.Media;

namespace SongRequest.Mobile;

/// <summary>
/// Android Auto media-browser entry point. Android Auto discovers this service
/// from the manifest, browses the app library/queue, and uses their shared
/// MediaSession for playback and transport controls.
/// </summary>
[Service(
    Name = "net.bycra.songrequest.CarMediaService",
    Exported = true,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
public sealed class CarMediaService : MediaBrowserService
{
    public const string RootId = "root";
    public const string LibraryId = "folder:library";
    public const string QueueId = "folder:queue";

    public override void OnCreate()
    {
        base.OnCreate();
        try { SessionToken = AndroidSessionHolder.Current; } catch { }
        CarMediaBridge.ReadyChanged += OnReadyChanged;
    }

    public override void OnDestroy()
    {
        CarMediaBridge.ReadyChanged -= OnReadyChanged;
        CarModeState.SetAndroidAutoConnected(false);
        AppHost.Current?.ApplyCarMode();
        base.OnDestroy();
    }

    private void OnReadyChanged()
    {
        try { NotifyChildrenChanged(RootId); } catch { }
    }

    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
    {
        // Binding from Android Auto / an automotive media client is the connection
        // signal that must switch the phone to the music-only operating mode.
        if (IsCarClient(clientPackageName) || CarModeState.IsUiModeCar)
        {
            CarModeState.SetAndroidAutoConnected(true);
            AppHost.Current?.ApplyCarMode();
        }

        // The car may browse the library; the media session controls playback.
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

    public override void OnLoadChildren(string parentId, Result? result)
    {
        if (result == null) return;

        var items = new List<MediaBrowser.MediaItem>();
        try
        {
            if (!CarMediaBridge.IsReady)
            {
                // The phone owns the startup/loading flow. The car surface only
                // shows this waiting entry until that flow completes.
                items.Add(Folder("waiting", "Waiting for SongRequest…",
                    "Loading on your phone"));
                result.SendResult(new JavaList<MediaBrowser.MediaItem>(items));
                return;
            }

            if (parentId == RootId)
            {
                items.Add(Folder(LibraryId, "Library", "All songs"));
                items.Add(Folder(QueueId, "Queue", "Up next"));
            }
            else if (parentId == LibraryId)
            {
                items.AddRange(CarMediaBridge.Library().Select(ToMediaItem));
            }
            else if (parentId == QueueId)
            {
                items.AddRange(CarMediaBridge.Queue().Select(ToMediaItem));
            }
        }
        catch { }

        result.SendResult(new JavaList<MediaBrowser.MediaItem>(items));
    }

    private static MediaBrowser.MediaItem Folder(string id, string title, string subtitle)
    {
        var description = new MediaDescription.Builder()
            .SetMediaId(id)
            .SetTitle(title)
            .SetSubtitle(subtitle)
            .Build();
        return new MediaBrowser.MediaItem(description, (MediaItemFlags)0);
    }

    private static MediaBrowser.MediaItem ToMediaItem(CarMediaEntry entry)
    {
        var builder = new MediaDescription.Builder()
            .SetMediaId(entry.Id)
            .SetTitle(entry.Title)
            .SetSubtitle(string.IsNullOrWhiteSpace(entry.Album)
                ? entry.Artist
                : entry.Artist + " · " + entry.Album);

        if (!string.IsNullOrWhiteSpace(entry.ArtworkPath) && System.IO.File.Exists(entry.ArtworkPath))
        {
            try { builder.SetIconUri(Android.Net.Uri.FromFile(new Java.IO.File(entry.ArtworkPath))!); } catch { }
        }

        var description = builder.Build();
        return new MediaBrowser.MediaItem(description, MediaItemFlags.Playable);
    }
}
