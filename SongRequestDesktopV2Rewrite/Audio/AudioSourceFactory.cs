namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IAudioSource"/> instances using the cross-platform FFmpeg
/// decoder, which handles every container/codec (including Opus-in-WebM files
/// saved as .mp3). FFmpeg is located in a bundled <c>tools</c> folder, next to
/// the app, or on the PATH.
/// </summary>
public static class AudioSourceFactory
{
    public static IAudioSource Open(string path)
    {
        var ffmpeg = FFmpegLocator.FindFfmpeg()
            ?? throw new System.IO.FileNotFoundException(
                "FFmpeg is required for audio playback but was not found. " +
                "Place it in the 'tools' folder next to the app or install it on the PATH.");

        return new FFmpegAudioSource(path, ffmpeg, FFmpegLocator.FindFfprobe());
    }
}
