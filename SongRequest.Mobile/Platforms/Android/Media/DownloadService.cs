using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace SongRequest.Mobile;

/// <summary>
/// Foreground service that keeps a download running while the app is in the
/// background (Android 12+ suspends plain background work aggressively).
///
/// It is started when the first download begins and stopped when the last one
/// finishes, with a low-key progress notification so the user can see that the app
/// is still working.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class DownloadService : Service
{
    private const string ChannelId = "songrequest.downloads";
    private const int NotificationId = 4712;

    private static readonly object Sync = new();
    private static int _activeDownloads;
    private static string _currentTitle = "Downloading songs";
    private static int _currentProgress;
    private static bool _running;

    /// <summary>Creates the notification channel (idempotent).</summary>
    public static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        try
        {
            var manager = (NotificationManager?)context.GetSystemService(NotificationService);
            if (manager == null || manager.GetNotificationChannel(ChannelId) != null) return;

            var channel = new NotificationChannel(ChannelId, "Downloads", NotificationImportance.Low)
            {
                Description = "Song downloads in progress",
            };
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);
        }
        catch (Exception ex)
        {
            Log.Debug("SongRequest", $"download channel failed: {ex.Message}");
        }
    }

    /// <summary>Called when a download starts.</summary>
    public static void OnDownloadStarted(Context context, string title)
    {
        lock (Sync)
        {
            _activeDownloads++;
            _currentTitle = title;
            _currentProgress = 0;
        }

        EnsureChannel(context);
        Start(context);
        Update(context, _currentProgress);
    }

    /// <summary>Called as a download progresses (0..1).</summary>
    public static void OnDownloadProgress(Context context, double progress)
    {
        var percent = (int)Math.Round(Math.Clamp(progress, 0, 1) * 100);
        lock (Sync) { _currentProgress = percent; }

        Update(context, percent);
    }

    /// <summary>Called when a download finishes (successfully or not).</summary>
    public static void OnDownloadFinished(Context context)
    {
        bool idle;
        lock (Sync)
        {
            _activeDownloads = Math.Max(0, _activeDownloads - 1);
            idle = _activeDownloads == 0;
        }

        if (idle) Stop(context);
        else Update(context, _currentProgress);
    }

    internal static DownloadService? Current { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        EnsureChannel(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var notification = BuildNotification();
            if (notification != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
                else
                    StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("SongRequest", $"download service start failed: {ex.Message}");
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        Current = null;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private static void Start(Context context)
    {
        lock (Sync) { if (_running) return; _running = true; }

        try
        {
            var intent = new Intent(context, typeof(DownloadService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception ex)
        {
            Log.Debug("SongRequest", $"download service start failed: {ex.Message}");
            lock (Sync) { _running = false; }
        }
    }

    private static void Stop(Context context)
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

            context.StopService(new Intent(context, typeof(DownloadService)));
        }
        catch (Exception ex)
        {
            Log.Debug("SongRequest", $"download service stop failed: {ex.Message}");
        }
    }

    private static void Update(Context context, int percent)
    {
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
            Log.Debug("SongRequest", $"download notification failed: {ex.Message}");
        }
    }

    private Notification? BuildNotification()
    {
        string title;
        int percent;
        int active;
        lock (Sync) { title = _currentTitle; percent = _currentProgress; active = _activeDownloads; }

        var builder = new Notification.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(active > 1 ? $"{active} downloads • {percent}%" : $"{percent}%");
        builder.SetOnlyAlertOnce(true);
        builder.SetOngoing(true);
        builder.SetProgress(100, percent, false);
        return builder.Build();
    }
}
