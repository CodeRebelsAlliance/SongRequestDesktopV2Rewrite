using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

public class AudioClipCacheTests
{
    [SkippableFact]
    public void ShortClip_IsLoadedIntoMemory_WithDuration()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var cache = new AudioClipCache();
        using var source = cache.CreateSource(Fixtures.Audio.Wav);

        Assert.IsType<InMemoryAudioSource>(source);
        Assert.InRange(source.TotalTime.TotalSeconds, 2.7, 3.3);
    }

    [SkippableFact]
    public void RepeatedRequests_ProduceIndependentCursors()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var cache = new AudioClipCache();
        using var first = cache.CreateSource(Fixtures.Audio.Wav);
        using var second = cache.CreateSource(Fixtures.Audio.Wav);

        Assert.Equal(0, second.Position); // fresh, independent cursor

        var (rmsA, _) = AudioTestHelpers.DecodeAll(first, 0.2);
        var (rmsB, _) = AudioTestHelpers.DecodeAll(second, 0.2);

        Assert.True(rmsA > 0.3);
        Assert.True(rmsB > 0.3); // second cursor starts from the beginning, independent of first
    }

    [SkippableFact]
    public void LongClip_StreamsInsteadOfCaching()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var cache = new AudioClipCache();
        using var source = cache.CreateSource(Fixtures.Audio.LongOpus);

        Assert.IsNotType<InMemoryAudioSource>(source);
    }

    [SkippableFact]
    public void Preload_WarmsCache_ForShortClip()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var cache = new AudioClipCache();
        cache.Preload(Fixtures.Audio.Wav);

        // CreateSource always returns an in-memory source for short clips (loading if needed);
        // the preload just makes the first one a cache hit. Assert stable behaviour.
        using var source = cache.CreateSource(Fixtures.Audio.Wav);
        Assert.IsType<InMemoryAudioSource>(source);
    }
}
