using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SongAnalysisTests
{
    [Fact]
    public void Analyzer_DetectsBpmLoudnessDropsAndQuality()
    {
        // 120 BPM click track with a quiet intro, then a loud drop at 5 s.
        var samples = BuildClickTrack(seconds: 10, bpm: 120, dropAt: 5);
        var source = new BufferSource(samples, 44100, 1);
        var analyzer = new SongAnalyzer(_ => source);
        var path = Path.Combine(TestSupport.NewTempDir("analysis-click"), "click.wav");
        File.WriteAllBytes(path, new byte[128]);

        var analysis = analyzer.Analyze(path, "Test", "Artist");

        Assert.InRange(analysis.Bpm, 112, 128);
        Assert.True(analysis.BpmConfidence > 0.1, $"confidence {analysis.BpmConfidence}");
        Assert.True(analysis.IntegratedLoudnessLufs < 0);
        Assert.True(analysis.PeakDb <= 0);
        Assert.True(analysis.DynamicRangeDb > 1);
        Assert.Equal(44100, analysis.Quality.SampleRate);
        Assert.Equal(1, analysis.Quality.Channels);
        Assert.False(string.IsNullOrWhiteSpace(analysis.Quality.Summary));
        Assert.True(analysis.Quality.Score > 0);
        Assert.InRange(analysis.Timeline.Count, 10, 2000);
        Assert.Contains(analysis.Drops, d => Math.Abs(d.Time - 5) < 2.5);
        Assert.Contains("FFT", analysis.AnalysisPath);
    }

    [Fact]
    public void Analyzer_DoesNotInventDropsInConstantAudio()
    {
        var samples = new float[44100 * 6];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 0.2);

        var path = Path.Combine(TestSupport.NewTempDir("analysis-constant"), "constant.wav");
        File.WriteAllBytes(path, new byte[128]);
        var analysis = new SongAnalyzer(_ => new BufferSource(samples, 44100, 1))
            .Analyze(path);

        Assert.True(analysis.Drops.Count <= 1,
            "constant audio should not produce a list of drops");
    }

    [Fact]
    public async Task Service_CachesFreshAnalysisAndPersistsIt()
    {
        var directory = TestSupport.NewTempDir("analysis");
        var cachePath = Path.Combine(directory, "cache.json");
        var samples = BuildClickTrack(seconds: 4, bpm: 100, dropAt: 2);
        int opens = 0;

        var service = new SongAnalysisService(_ =>
        {
            opens++;
            return new BufferSource(samples, 44100, 1);
        }, cachePath);

        var path = Path.Combine(directory, "song.wav");
        File.WriteAllBytes(path, new byte[1024]);

        var first = await service.AnalyzeAsync(path, "Song", "Artist");
        var second = await service.AnalyzeAsync(path, "Song", "Artist");

        Assert.Same(first, second);
        Assert.Equal(1, opens);
        Assert.True(File.Exists(cachePath));

        var reloaded = new SongAnalysisService(_ => throw new InvalidOperationException("must use cache"), cachePath);
        var cached = reloaded.GetFresh(path);
        Assert.NotNull(cached);
        Assert.Equal(first.Bpm, cached!.Bpm);
        Assert.Equal(first.SongPath, cached.SongPath);
    }

    [Fact]
    public async Task Service_ForceReanalyses()
    {
        var directory = TestSupport.NewTempDir("analysis-force");
        var cachePath = Path.Combine(directory, "cache.json");
        var samples = BuildClickTrack(seconds: 3, bpm: 120, dropAt: 1.5);
        int opens = 0;
        var service = new SongAnalysisService(_ =>
        {
            opens++;
            return new BufferSource(samples, 44100, 1);
        }, cachePath);

        var path = Path.Combine(directory, "song.wav");
        File.WriteAllBytes(path, new byte[64]);

        await service.AnalyzeAsync(path);
        await service.AnalyzeAsync(path, force: true);

        Assert.Equal(2, opens);
    }

    private static float[] BuildClickTrack(int seconds, double bpm, double dropAt)
    {
        int sampleRate = 44100;
        var samples = new float[sampleRate * seconds];
        int clickLength = (int)(sampleRate * 0.05);
        double interval = 60.0 / bpm;
        int clicks = (int)(seconds / interval);

        for (int click = 0; click < clicks; click++)
        {
            double time = click * interval;
            int start = (int)(time * sampleRate);
            double amplitude = time >= dropAt ? 0.75 : 0.08;
            for (int i = 0; i < clickLength && start + i < samples.Length; i++)
            {
                double envelope = Math.Exp(-i / (sampleRate * 0.012));
                samples[start + i] += (float)(Math.Sin(2 * Math.PI * 180 * i / sampleRate) * envelope * amplitude);
            }
        }

        return samples;
    }

    /// <summary>Finite decoded source used by the analyzer tests.</summary>
    private sealed class BufferSource : IAudioSource, ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public BufferSource(float[] samples, int sampleRate, int channels)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_samples.Length / (WaveFormat.SampleRate * WaveFormat.Channels));
        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)_position / (WaveFormat.SampleRate * WaveFormat.Channels));
            set => _position = Math.Clamp((int)(value.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels), 0, _samples.Length);
        }
        public long Length => _samples.Length * 4L;
        public long Position { get => _position * 4L; set => _position = (int)Math.Clamp(value / 4L, 0, _samples.Length); }
        public float Volume { get; set; } = 1f;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public ISampleProvider ToSampleProvider() => this;
        public void Dispose() { }
    }
}
