using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class KaraokeVersionTests
{
    [Fact]
    public async Task VocalRemover_AttenuatesCenteredVocalAndKeepsSides()
    {
        var dir = TestSupport.NewTempDir("vocal-remover");
        var sourcePath = Path.Combine(dir, "source.wav");
        File.WriteAllBytes(sourcePath, new byte[128]);
        var samples = BuildStereo(6);
        var previous = AudioSourceFactory.Provider;
        AudioSourceFactory.Provider = _ => new StereoSource(samples, 44100, 2);
        try
        {
            var output = Path.Combine(dir, "karaoke.wav");
            var progress = new List<double>();
            VocalRemover.Process(sourcePath, output, (p, _) => progress.Add(p));
            Assert.True(File.Exists(output));

            var (centerIn, sideIn) = Centers(samples);
            var decoded = ReadWav(output);
            var (centerOut, sideOut) = Centers(decoded);

            Assert.True(centerOut < centerIn * 0.25,
                $"centered vocal should drop (in {centerIn:F4}, out {centerOut:F4})");
            Assert.True(sideOut > 0.03, $"side content should remain (out {sideOut:F4})");
            Assert.NotEmpty(progress);
            Assert.Equal(1.0, progress[^1], 2);
        }
        finally
        {
            AudioSourceFactory.Provider = previous;
        }
    }

    [Fact]
    public async Task Service_LinksVersionedKaraokeFileAndDeletesItWithTheSong()
    {
        var dir = TestSupport.NewTempDir("karaoke-service");
        var sourcePath = Path.Combine(dir, "song.wav");
        File.WriteAllBytes(sourcePath, new byte[128]);
        var samples = BuildStereo(4);
        var previous = AudioSourceFactory.Provider;
        AudioSourceFactory.Provider = _ => new StereoSource(samples, 44100, 2);
        try
        {
            var service = new KaraokeVersionService(
                Path.Combine(dir, "index.json"),
                Path.Combine(dir, "karaoke"));

            var first = await service.EnsureAsync(sourcePath, "Song", "Artist");
            Assert.NotNull(first);
            Assert.True(File.Exists(first!.KaraokePath));
            Assert.Equal(VocalRemover.AlgorithmVersion, first.Version);
            Assert.False(service.NeedsGeneration(sourcePath));
            Assert.NotNull(service.GetCurrent(sourcePath));

            var second = await service.EnsureAsync(sourcePath, "Song", "Artist");
            Assert.Equal(first.KaraokePath, second!.KaraokePath);

            // Simulate an algorithm upgrade in the persisted index.
            var indexPath = Path.Combine(dir, "index.json");
            var entries = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(indexPath));
            entries[0]!["Version"] = "0.0";
            File.WriteAllText(indexPath, entries.ToString());
            var upgraded = new KaraokeVersionService(indexPath, Path.Combine(dir, "karaoke"));
            Assert.True(upgraded.NeedsGeneration(sourcePath));
            var regenerated = await upgraded.EnsureAsync(sourcePath, "Song", "Artist");
            Assert.Equal(VocalRemover.AlgorithmVersion, regenerated!.Version);

            Assert.True(upgraded.Delete(sourcePath));
            Assert.False(File.Exists(first.KaraokePath));
            Assert.Null(upgraded.GetCurrent(sourcePath));
        }
        finally
        {
            AudioSourceFactory.Provider = previous;
        }
    }

    [Fact]
    public async Task PlaybackWaitsForKaraokePreparationBeforeStarting()
    {
        var dir = TestSupport.NewTempDir("karaoke-playback");
        var sourcePath = Path.Combine(dir, "song.wav");
        File.WriteAllBytes(sourcePath, new byte[128]);
        var samples = BuildStereo(3);
        var previous = AudioSourceFactory.Provider;
        AudioSourceFactory.Provider = _ => new StereoSource(samples, 44100, 2);
        try
        {
            var engine = new MusicEngine
            {
                AutoTick = false,
                ConfigAccessor = () => new Config { NormalizeVolume = false }
            };
            engine.OutputFactory = () => new FakeAudioOutput();
            engine.AddSong(new Song("Karaoke", "Artist", null, TimeSpan.FromSeconds(3), sourcePath));

            var prepared = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.PreparePlaybackPathAsync = (_, _) => prepared.Task;
            engine.PlaySong(engine.Queue[0]);

            await Task.Delay(80);
            Assert.Null(engine.Output);

            prepared.SetResult(sourcePath);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (engine.Output == null && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.NotNull(engine.Output);
            Assert.True(engine.IsPlaybackActive);
        }
        finally
        {
            AudioSourceFactory.Provider = previous;
        }
    }

    private static float[] BuildStereo(int seconds)
    {
        const int rate = 44100;
        var samples = new float[rate * seconds * 2];
        for (int i = 0; i < rate * seconds; i++)
        {
            double t = (double)i / rate;
            double vocal = 0.30 * Math.Sin(2 * Math.PI * 440 * t);   // center
            double side = 0.22 * Math.Sin(2 * Math.PI * 220 * t);    // out of phase
            samples[i * 2] = (float)(vocal + side);
            samples[i * 2 + 1] = (float)(vocal - side);
        }
        return samples;
    }

    private static (double Center, double Side) Centers(float[] interleaved)
    {
        double center = 0, side = 0;
        int frames = interleaved.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            double l = interleaved[i * 2];
            double r = interleaved[i * 2 + 1];
            center += ((l + r) * 0.5) * ((l + r) * 0.5);
            side += ((l - r) * 0.5) * ((l - r) * 0.5);
        }
        return (Math.Sqrt(center / Math.Max(1, frames)), Math.Sqrt(side / Math.Max(1, frames)));
    }

    private static float[] ReadWav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(12); // RIFF/WAVE
        reader.ReadBytes(24); // fmt chunk (PCM, 16 bytes)
        reader.ReadBytes(8);  // data marker + size
        var data = reader.ReadBytes((int)(stream.Length - 44));
        var samples = new float[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(data, i * 2) / (float)short.MaxValue;
        return samples;
    }

    private sealed class StereoSource : IAudioSource, ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public StereoSource(float[] samples, int rate, int channels)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
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
