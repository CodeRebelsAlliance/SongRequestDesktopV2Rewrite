namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Creates <see cref="IAudioOutput"/> instances using OpenAL Soft on every
/// platform.
/// </summary>
public static class AudioOutputFactory
{
    public static IAudioOutput Create() => new OpenAlAudioOutput();
}
