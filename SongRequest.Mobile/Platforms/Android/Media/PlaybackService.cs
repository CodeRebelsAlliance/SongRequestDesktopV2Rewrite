using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using AndroidMetadata = Android.Media.MediaMetadata;
using AndroidPlaybackState = Android.Media.Session.PlaybackState;

namespace SongRequest.Mobile;

/// <summary>
/// Foreground playback service: keeps the process (and therefore the audio
/// track) alive while the app is backgrounded, and owns the media notification
/// whose actions mirror the transport controls.
///
/// It is started/stopped by the media session as the playback state changes, and
/// forwards notification taps to the session through <see cref="CommandReceived"/>.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    public const string ActionPlay = "net.bycra.songrequest.PLAY";
    public const string ActionPause = "net.bycra.songrequest.PAUSE";
    public const string ActionToggle = "net.bycra.songrequest.TOGGLE";
    public const string ActionNext = "net.bycra.songrequest.NEXT";
    public const string ActionPrevious = "net.bycra.songrequest.PREVIOUS";
    public const string ActionStop = "net.bycra.songrequest.STOP";
    public const string ActionQuit = "net.bycra.songrequest.QUIT";

    private const string ChannelId = "songrequest.playback";
    private const int NotificationId = 4711;

    private static readonly object Sync = new();
    private static AndroidMetadata? _metadata;
    private static SongRequestDesktopV2Rewrite.MediaPlaybackState _state = SongRequestDesktopV2Rewrite.MediaPlaybackState.Stopped;
    private static TimeSpan _position;
    private static bool _running;

    /// <summary>Raised when the notification's actions are used.</summary>
    public static event EventHandler<string>? CommandReceived;

    /// <summary>Creates the notification channel (idempotent).</summary>
    public static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        try
        {
            var manager = (NotificationManager?)context.GetSystemService(NotificationService);
            if (manager == null) return;
            if (manager.GetNotificationChannel(ChannelId) != null) return;

            var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
            {
                Description = "Now playing controls",
            };
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Notification channel failed: {ex.Message}");
        }
    }

    public static void Start(Context context)
    {
        lock (Sync) { if (_running) return; _running = true; }

        try
        {
            var intent = new Intent(context, typeof(PlaybackService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Playback service start failed: {ex.Message}");
            lock (Sync) { _running = false; }
        }
    }

    public static void Stop(Context context)
    {
        lock (Sync) { if (!_running) return; _running = false; }

        try
        {
            var service = Current;
            if (service != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24)) service.StopForeground(StopForegroundFlags.Remove);
                else service.StopForeground(true);
            }

            context.StopService(new Intent(context, typeof(PlaybackService)));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Playback service stop failed: {ex.Message}");
        }
    }

    /// <summary>Refreshes the notification from the current session state.</summary>
    public static void Update(AndroidMetadata? metadata, SongRequestDesktopV2Rewrite.MediaPlaybackState state, TimeSpan position)
    {
        lock (Sync)
        {
            _metadata = metadata;
            _state = state;
            _position = position;
        }

        var service = Current;
        if (service == null) return;

        try
        {
            var notification = service.BuildNotification();
            if (notification != null)
                ((NotificationManager?)service.GetSystemService(NotificationService))?.Notify(NotificationId, notification);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Notification update failed: {ex.Message}");
        }
    }

    internal static PlaybackService? Current { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        EnsureChannel(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (!string.IsNullOrEmpty(action))
        {
            if (action == ActionStop || action == ActionQuit)
            {
                // Let the session decide what "stop" means (the engine may want to
                // keep its queue); we only tear the foreground state down.
                CommandReceived?.Invoke(this, action);
                Stop(this);
                return StartCommandResult.NotSticky;
            }

            CommandReceived?.Invoke(this, action);
        }

        try
        {
            var notification = BuildNotification();
            if (notification != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
                else
                    StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"StartForeground failed: {ex.Message}");
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        Current = null;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private Notification? BuildNotification()
    {
        AndroidMetadata? metadata;
        SongRequestDesktopV2Rewrite.MediaPlaybackState state;
        lock (Sync) { metadata = _metadata; state = _state; }

        var session = AndroidSessionHolder.Current;
        if (session == null) return null;

        var title = metadata?.GetString(AndroidMetadata.MetadataKeyTitle) ?? "SongRequest";
        var artist = metadata?.GetString(AndroidMetadata.MetadataKeyArtist) ?? string.Empty;

        var style = new Notification.MediaStyle();
        style.SetMediaSession(session);
        style.SetShowActionsInCompactView(0, 1, 2);

        var builder = new Notification.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(artist);
        builder.SetOnlyAlertOnce(true);
        builder.SetOngoing(state == SongRequestDesktopV2Rewrite.MediaPlaybackState.Playing);
        builder.SetVisibility(NotificationVisibility.Public);
        builder.SetStyle(style);

        var large = metadata?.GetBitmap(AndroidMetadata.MetadataKeyAlbumArt);
        if (large != null) builder.SetLargeIcon(large!);

        var contentIntent = PendingIntent.GetActivity(
            this, 0, LaunchIntent(), PendingIntentFlags.Immutable);
        if (contentIntent != null) builder.SetContentIntent(contentIntent!);

        var playPause = state == SongRequestDesktopV2Rewrite.MediaPlaybackState.Playing
            ? (Icon: Android.Resource.Drawable.IcMediaPause, Action: ActionPause, Title: "Pause")
            : (Icon: Android.Resource.Drawable.IcMediaPlay, Action: ActionPlay, Title: "Play");

        builder.AddAction(MakeAction(Android.Resource.Drawable.IcMediaPrevious, "Previous", ActionPrevious, 1));
        builder.AddAction(MakeAction(playPause.Icon, playPause.Title, playPause.Action, 2));
        builder.AddAction(MakeAction(Android.Resource.Drawable.IcMediaNext, "Next", ActionNext, 3));

        return builder.Build();
    }

    private Notification.Action MakeAction(int icon, string title, string action, int requestCode)
    {
        var intent = new Intent(this, typeof(PlaybackService)).SetAction(action);
        var pending = PendingIntent.GetService(this, requestCode, intent, PendingIntentFlags.Immutable);
        return new Notification.Action.Builder(
            Icon.CreateWithResource(this, icon), new Java.Lang.String(title), pending!).Build()!;
    }

    private Intent LaunchIntent()
    {
        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        if (intent != null) return intent;

        intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop);
        return intent;
    }
}

/// <summary>Hands the media session token to the notification builder.</summary>
internal static class AndroidSessionHolder
{
    public static Android.Media.Session.MediaSession.Token? Current { get; set; }
}
