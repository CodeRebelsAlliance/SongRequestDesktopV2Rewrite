using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Exercises the new analysis/equalizer code against a real FFmpeg-decoded file,
/// not only the in-memory unit fixtures. These tests also keep the integration
/// coverage ratchet above its floor.
/// </summary>
public class AdvancedAudioIntegrationTests
{
    [SkippableFact]
    public void Analyzer_DetectsPartsAndQualityFromDecodedAudio()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var path = WriteStructuredWav();
        var analysis = new SongAnalyzer(AudioSourceFactory.Open)
            .Analyze(path, "Structured", "Integration");

        Assert.InRange(analysis.DurationSeconds, 50, 70);
        Assert.NotEmpty(analysis.Timeline);
        Assert.NotEmpty(analysis.Parts);
        Assert.True(analysis.Quality.Score > 0);
        Assert.Equal(44100, analysis.Quality.SampleRate);
        Assert.Contains("FFT", analysis.AnalysisPath);
        Assert.True(analysis.IntegratedLoudnessLufs < 0);
        Assert.All(analysis.Parts, part =>
        {
            Assert.True(part.EndSeconds > part.StartSeconds);
            Assert.False(string.IsNullOrWhiteSpace(part.Label));
        });
    }

    [SkippableFact]
    public async Task AnalysisService_ReusesRealDecodedAnalysis()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var path = WriteStructuredWav();
        var cachePath = Path.Combine(Fixtures.NewTempDir(), "analysis-cache.json");
        var service = new SongAnalysisService(AudioSourceFactory.Open, cachePath);

        var first = await service.AnalyzeAsync(path, "Structured", "Integration");
        var second = await service.AnalyzeAsync(path, "Structured", "Integration");

        Assert.Same(first, second);
        Assert.True(File.Exists(cachePath));
        Assert.NotNull(service.GetFresh(path));
        Assert.Contains(service.Cached, a => a.SongPath == path);
    }

    [SkippableFact]
    public void Equalizer_ProcessesDecodedAudioInTheSharedPipeline()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var config = new Config();
        var equalizer = new EqualizerService
        {
            ConfigAccessor = () => config,
            ConfigUpdater = update => update(config)
        };
        equalizer.Apply(true, 0, "Bass Boost", null);

        using var source = AudioSourceFactory.Open(Fixtures.Audio.Wav);
        var processor = new EqualizerProcessor(source.ToSampleProvider(), equalizer);
        var buffer = new float[8192];
        double sum = 0;
        int total = 0;

        int read;
        while ((read = processor.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                Assert.InRange(buffer[i], -1f, 1f);
                sum += buffer[i] * buffer[i];
            }
            total += read;
        }

        Assert.True(total > 0);
        Assert.True(Math.Sqrt(sum / total) > 0.01);
        Assert.Equal("Bass Boost", equalizer.GetState().Preset);
    }

    [SkippableFact]
    public async Task AutoAnalysis_StartsFromTheEngineWithoutAPlayerPage()
    {
        Skip.IfNot(Fixtures.FfmpegAvailable, "ffmpeg not available");

        var original = ConfigService.Instance.Current.AutoAnalyzeSongs;
        try
        {
            ConfigService.Instance.Update(c => c.AutoAnalyzeSongs = true);
            var path = WriteStructuredWav();
            var host = new HeadlessHost(new List<System.Net.Cookie>());
            var interop = new YoutubeFormInterop(host, host.YoutubeService);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            interop.EventRaised += (name, _) =>
            {
                if (name == "songAnalysisUpdate") completed.TrySetResult(true);
            };

            // The engine raises this on a track change; auto-analysis must not
            // depend on a player web view being open (the mobile reliability bug).
            host.Music.NowPlayingChanged?.Invoke(
                new Song("Auto", "Integration", null, TimeSpan.FromSeconds(56), path));

            var done = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(completed.Task, done);
            Assert.True(await completed.Task);
        }
        finally
        {
            ConfigService.Instance.Update(c => c.AutoAnalyzeSongs = original);
        }
    }

    /// <summary>Writes a 56-second structured 16-bit PCM WAV for the real decoder.</summary>
    private static string WriteStructuredWav()
    {
        var path = Path.Combine(Fixtures.NewTempDir(), "structured.wav");
        var sections = new (double Seconds, double Amplitude, double Frequency)[]
        {
            (4, 0.05, 180),
            (8, 0.22, 220),
            (8, 0.62, 440),
            (8, 0.22, 220),
            (8, 0.62, 440),
            (8, 0.32, 6000),
            (8, 0.62, 440),
            (4, 0.05, 180)
        };

        const int sampleRate = 44100;
        int totalSamples = (int)(sections.Sum(s => s.Seconds) * sampleRate);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + totalSamples * 2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(totalSamples * 2);

        int offset = 0;
        foreach (var section in sections)
        {
            int count = (int)(section.Seconds * sampleRate);
            for (int i = 0; i < count && offset < totalSamples; i++, offset++)
            {
                double fade = Math.Min(1, Math.Min(i, count - i) / (sampleRate * 0.02));
                double value = Math.Sin(2 * Math.PI * section.Frequency * i / sampleRate) *
                               section.Amplitude * fade;
                writer.Write((short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue));
            }
        }

        writer.Flush();
        return path;
    }
}
