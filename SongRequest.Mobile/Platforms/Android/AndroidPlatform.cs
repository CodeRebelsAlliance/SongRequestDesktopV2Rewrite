using System;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Registers the Android implementations of the core's platform seams:
/// <c>AudioTrack</c> for playback, <c>MediaExtractor</c>/<c>MediaCodec</c> for
/// decoding, the device list from <c>AudioManager</c>, and the framework
/// <c>MediaSession</c> for the lock screen / notification / headset buttons.
/// </summary>
internal static class AndroidPlatform
{
    public static void Register()
    {
        // ── Playback ──────────────────────────────────────────────────────────
        AudioOutputFactory.Provider = () => new AndroidAudioOutput();
        AudioOutputFactory.DeviceEnumerator = AndroidAudioDevices.Enumerate;

        // ── Decode: the platform's own demuxer + decoder ──────────────────────
        AudioSourceFactory.Provider = path => new AndroidAudioSource(path);

        // ── System media controls (lock screen, notification, headset) ────────
        MediaSessionFactory.Register(new AndroidMediaSessionBackend());

        // ── Album artwork: the platform decoder instead of FFmpeg ─────────────
        ThumbnailStore.Instance.Downscale = AndroidArtworkConverter.Downscale;
        ThumbnailStore.Instance.ConverterAvailable = () => true;

        // ── MIDI: MidiManager (USB + Bluetooth LE controllers) ────────────────
        MidiServiceFactory.BackendSupported = () => true;
        MidiServiceFactory.BackendFactory = () => new AndroidMidiService();

        // ── Announcements: the chime lives in the app's data root ─────────────
        EnsureChimeFile();

        // ── Downloads: AAC-in-MP4 (m4a) so the platform decoders can play it,
        //    and no external tools to install on a phone. ─────────────────────
        DownloadedAudio.PreferredContainer = "mp4";
        ToolManager.IsSupported = false;

        // MIDI arrives in M4 (Android MidiManager).
    }

    /// <summary>
    /// Copies the announcement chime out of the app package into the data root,
    /// where <see cref="AppPaths.ChimePath"/> expects it (the package itself is
    /// not a readable file path on Android).
    /// </summary>
    internal static void EnsureChimeFile()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var target = AppPaths.ChimePath;
                if (System.IO.File.Exists(target) && new System.IO.FileInfo(target).Length > 0) return;

                using var source = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync("announcement.mp3");
                using var destination = System.IO.File.Create(target);
                await source.CopyToAsync(destination);
                Android.Util.Log.Debug("SongRequest", $"announcement chime extracted to {target}");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"announcement chime unavailable: {ex.Message}");
            }
        });
    }

    private static int _notificationPermissionRequested;

    /// <summary>
    /// Asks for the notification permission once, the first time playback starts.
    /// </summary>
    internal static void RequestNotificationPermissionOnce()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (System.Threading.Interlocked.Exchange(ref _notificationPermissionRequested, 1) == 1) return;

        try
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                // The API-level check has to be visible at the call site.
                if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;

                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                activity?.RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 5);
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"notification permission request failed: {ex.Message}");
        }
    }
}
