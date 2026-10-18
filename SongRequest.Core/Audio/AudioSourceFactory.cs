using System;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IAudioSource"/> instances.
///
/// The default backend is the cross-platform FFmpeg decoder (a bundled
/// <c>tools</c> folder, next to the app, or on the PATH), which handles every
/// container/codec — including the Opus-in-WebM files YouTube downloads that are
/// saved with a <c>.mp3</c> extension. Mobile hosts register their own backend
/// (platform decoders) through <see cref="Provider"/>.
/// </summary>
public static class AudioSourceFactory
{
    /// <summary>Opens a decoded audio source. Registered by the host (optional).</summary>
    public static Func<string, IAudioSource>? Provider { get; set; }

    public static IAudioSource Open(string path)
        => Provider?.Invoke(path) ?? OpenWithFFmpeg(path);

    private static IAudioSource OpenWithFFmpeg(string path)
    {
        var ffmpeg = FFmpegLocator.FindFfmpeg()
            ?? throw new System.IO.FileNotFoundException(
                "FFmpeg is required for audio playback but was not found. " +
                "Place it in the 'tools' folder next to the app or install it on the PATH.");

        return new FFmpegAudioSource(path, ffmpeg, FFmpegLocator.FindFfprobe());
    }
}
