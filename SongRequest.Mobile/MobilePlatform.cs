using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Registers the mobile implementations of the core's platform seams.
///
/// M1 (this milestone) intentionally registers nothing: the core's silent
/// defaults (NullAudioOutput, NullMidiService, no media session, no tray) keep
/// the whole UI — requests, library, home feed, soundboard model, settings —
/// fully usable without an audio backend, which is what the shell/bridge
/// milestone verifies. Later milestones plug in here:
///
///   M2  audio output (AudioTrack / AVAudioEngine) + decode (MediaCodec / AVAssetReader),
///       output device enumeration, the playback foreground service and the media session
///   M4  MIDI in/out (Android MidiManager / CoreMIDI)
///   M5  the YouTube downloader's platform bits and the in-app cookie login
///   M6  the stage: external display / casting
/// </summary>
internal static class MobilePlatform
{
    public static void Register()
    {
        // A phone or tablet feeding a TV/HDMI display is much smaller than the
        // desktop window, so the presentation starts at 50% unless the user has
        // already chosen a zoom.
        try
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg != null && !cfg.PresentationZoomCustomized)
                ConfigService.Instance.Update(c => c.PresentationZoom = 0.5);
        }
        catch
        {
            // Defaults are optional; never block startup.
        }

#if ANDROID
        // Android updates itself from the release APK: download it, then let the
        // system package installer take over (the user confirms there).
        UpdateService.SelfUpdateSupported = true;
        UpdateService.PackageInstaller = (url, progress) => AndroidApkUpdater.InstallAsync(url, progress);

        AndroidPlatform.Register();
#elif IOS
        // iOS updates belong to the App Store, so the app must not offer an
        // in-app install. (IosPlatform.Register() arrives in M7.)
        UpdateService.SelfUpdateSupported = false;
#endif
    }
}
