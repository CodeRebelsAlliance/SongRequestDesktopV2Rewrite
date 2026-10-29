using NAudio.Wave;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The analysis command must work through the same dispatcher the HTTP API/MCP
/// use, not only from the player UI.
/// </summary>
public class AdvancedAudioCommandTests
{
    [Fact]
    public async Task AnalyzeSongCommand_ReturnsBpmLoudnessDropsAndTimeline()
    {
        var directory = TestSupport.NewTempDir("analysis-command");
        var path = Path.Combine(directory, "command.wav");
        File.WriteAllBytes(path, new byte[256]);

        var samples = new float[44100 * 6];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Sin(2 * Math.PI * 220 * i / 44100.0) * (i > samples.Length / 2 ? 0.6 : 0.05));

        var previous = AudioSourceFactory.Provider;
        AudioSourceFactory.Provider = _ => new BufferSource(samples, 44100, 1);
        try
        {
            var host = new HeadlessHost(new List<System.Net.Cookie>());
            var interop = new YoutubeFormInterop(host, host.YoutubeService);

            var result = await interop.ExecuteCommandAsync("analyzeSong", new JObject
            {
                ["songPath"] = path,
                ["force"] = true
            });

            Assert.True(result?["success"]?.ToObject<bool>());
            Assert.True(result?["bpm"]?.ToObject<double>() >= 0);
            Assert.True(result?["loudnessLufs"]?.ToObject<double>() < 0);
            Assert.NotNull(result?["quality"]?["score"]);
            Assert.NotNull(result?["quality"]?["sampleRate"]);
            Assert.True((result?["timeline"] as JArray)?.Count > 0);
            Assert.True((result?["parts"] as JArray)?.Count > 0);
            Assert.NotNull(result?["analysisPath"]?.ToString());

            var cached = await interop.ExecuteCommandAsync("getSongAnalysis", new JObject
            {
                ["songPath"] = path
            });
            Assert.Equal(result?["bpm"]?.ToObject<double>(), cached?["bpm"]?.ToObject<double>());

            var cache = await interop.ExecuteCommandAsync("getSongAnalysisCache");
            Assert.NotNull(cache?["analyses"] as JArray);
        }
        finally
        {
            AudioSourceFactory.Provider = previous;
        }
    }

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
        public TimeSpan CurrentTime { get => TimeSpan.Zero; set { } }
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
