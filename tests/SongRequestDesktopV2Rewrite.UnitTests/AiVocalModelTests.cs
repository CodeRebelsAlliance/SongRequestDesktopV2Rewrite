using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class AiVocalModelTests
{
    [Fact]
    public void Catalog_ExposesSizesRatingsAndOneRecommendedModel()
    {
        Assert.NotEmpty(AiVocalModelCatalog.All);
        Assert.Contains(AiVocalModelCatalog.All, m => m.Recommended);
        Assert.All(AiVocalModelCatalog.All, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.Contains("UVR", model.Name);
            Assert.EndsWith(".onnx", model.FileName);
            Assert.StartsWith("https://", model.DownloadUrl);
            Assert.True(model.SizeBytes > 20_000_000);
            Assert.InRange(model.QualityRating, 1, 5);
            Assert.InRange(model.PerformanceRating, 1, 5);
            Assert.Contains("Quality", model.RatingLabel);
            Assert.Contains("Speed", model.RatingLabel);
        });

        Assert.NotNull(AiVocalModelCatalog.Find(AiVocalModelCatalog.DefaultId));
        Assert.Null(AiVocalModelCatalog.Find("does-not-exist"));
    }

    [Fact]
    public void FormatBytes_IsHumanReadable()
    {
        Assert.Equal("0 B", AiVocalModelManager.FormatBytes(0));
        Assert.Equal("512 B", AiVocalModelManager.FormatBytes(512));
        Assert.Equal("1.0 KB", AiVocalModelManager.FormatBytes(1024));
        Assert.Equal("50.3 MB", AiVocalModelManager.FormatBytes(52_787_030));
        Assert.Equal("1.0 GB", AiVocalModelManager.FormatBytes(1024L * 1024 * 1024));
    }

    [Fact]
    public async Task Manager_InstallsListsAndDeletesAModelWithInjectedDownloader()
    {
        var dir = TestSupport.NewTempDir("ai-models");
        var indexPath = Path.Combine(dir, "models.json");
        var progress = new List<string>();
        var manager = new AiVocalModelManager(
            dir,
            indexPath,
            (url, destination, onProgress, ct) =>
            {
                File.WriteAllText(destination, "fake model");
                onProgress?.Invoke(50);
                onProgress?.Invoke(100);
                return Task.CompletedTask;
            });

        bool ok = await manager.InstallAsync(AiVocalModelCatalog.DefaultId,
            p => progress.Add(p.Stage));

        Assert.True(ok);
        Assert.True(manager.IsInstalled(AiVocalModelCatalog.DefaultId));
        Assert.NotNull(manager.GetModelPath(AiVocalModelCatalog.DefaultId));
        Assert.True(File.Exists(indexPath));
        Assert.Contains("downloading", progress);
        Assert.Contains("verifying", progress);
        Assert.Contains("installing", progress);
        Assert.Contains("done", progress);

        var status = manager.GetStatus().Single(s => s.Model.Id == AiVocalModelCatalog.DefaultId);
        Assert.True(status.Installed);
        Assert.True(status.InstalledBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(status.Sha256));

        Assert.True(manager.Delete(AiVocalModelCatalog.DefaultId));
        Assert.False(manager.IsInstalled(AiVocalModelCatalog.DefaultId));
    }

    [Fact]
    public void SherpaBackend_IsAvailableOnDesktop()
    {
        var separator = new SherpaAiVocalSeparator();
        Assert.True(separator.IsSupported);
        Assert.Contains("sherpa", separator.BackendName, StringComparison.OrdinalIgnoreCase);
        Assert.Same(separator.GetType(), AiVocalSeparationFactory.TryCreate()?.GetType());
    }

    [Fact]
    public void VersionFor_IncludesTheModelId()
    {
        Assert.Equal("3.0-ai:uvr-kara-2", AiVocalRemover.VersionFor("uvr-kara-2"));
        Assert.NotEqual(VocalRemover.AlgorithmVersion, AiVocalRemover.VersionFor("uvr-kara-2"));
    }

    [Fact]
    public async Task Service_UsesAiMetadataAndInvalidatesWhenTheModelChanges()
    {
        var dir = TestSupport.NewTempDir("ai-service");
        var source = Path.Combine(dir, "song.wav");
        File.WriteAllText(source, "source");
        var model = AiVocalModelCatalog.Default;
        var modelPath = Path.Combine(dir, model.FileName);
        File.WriteAllText(modelPath, "model");

        var service = new KaraokeVersionService(
            Path.Combine(dir, "index.json"),
            Path.Combine(dir, "karaoke"))
        {
            ConfigAccessor = () => new Config
            {
                EnhancedVocalRemoval = true,
                AiVocalModelId = model.Id
            },
            AiModelProvider = () => new AiVocalModelStatus
            {
                Model = model,
                Installed = true,
                Path = modelPath,
                InstalledBytes = new FileInfo(modelPath).Length
            },
            AiBackendProvider = () => new FakeAiSeparator()
        };

        var generated = await service.EnsureAsync(source, "Song", "Artist");

        Assert.NotNull(generated);
        Assert.Equal(AiVocalRemover.VersionFor(model.Id), generated!.Version);
        Assert.Equal("ai", generated.Separator);
        Assert.Equal(model.Id, generated.ModelId);
        Assert.Equal(model.Name, generated.ModelName);
        Assert.Equal(model.SizeBytes, generated.ModelSizeBytes);
        Assert.True(generated.GenerationSeconds >= 0);
        Assert.False(generated.FallbackFromAi);
        Assert.NotNull(service.GetCurrent(source));

        // Disabling AI changes the expected version back to the built-in one.
        service.ConfigAccessor = () => new Config
        {
            EnhancedVocalRemoval = false,
            AiVocalModelId = "uvr-kara-fast"
        };
        Assert.Null(service.GetCurrent(source));
    }

    [Fact]
    public async Task Service_RecordsFallbackWhenAiFails_AndRetriesAfterReset()
    {
        var dir = TestSupport.NewTempDir("ai-fallback");
        var source = Path.Combine(dir, "song.wav");
        File.WriteAllText(source, "source");
        var model = AiVocalModelCatalog.Default;
        var modelPath = Path.Combine(dir, model.FileName);
        File.WriteAllText(modelPath, "model");

        var previous = AudioSourceFactory.Provider;
        AudioSourceFactory.Provider = _ => new FakeStereoSource();
        try
        {
            var service = new KaraokeVersionService(
                Path.Combine(dir, "index.json"),
                Path.Combine(dir, "karaoke"))
            {
                ConfigAccessor = () => new Config
                {
                    EnhancedVocalRemoval = true,
                    AiVocalModelId = model.Id
                },
                AiModelProvider = () => new AiVocalModelStatus
                {
                    Model = model,
                    Installed = true,
                    Path = modelPath
                },
                AiBackendProvider = () => new FailingAiSeparator()
            };

            var generated = await service.EnsureAsync(source, "Song", "Artist");

            Assert.NotNull(generated);
            Assert.Equal(VocalRemover.AlgorithmVersion, generated!.Version);
            Assert.Equal("spectral", generated.Separator);
            Assert.True(generated.FallbackFromAi);
            Assert.Equal(model.Id, generated.FallbackModelId);
            Assert.Contains("fake AI failure", generated.AiError);

            // The fallback is accepted so playback does not retry a broken model
            // before every song.
            Assert.NotNull(service.GetCurrent(source));

            // Reinstalling/clearing the model fallback makes it stale again.
            Assert.Equal(1, service.ClearAiFallbacks(model.Id));
            Assert.Null(service.GetCurrent(source));
        }
        finally
        {
            AudioSourceFactory.Provider = previous;
        }
    }

    private sealed class FakeAiSeparator : IAiVocalSeparator
    {
        public bool IsSupported => true;
        public string BackendName => "fake-ai";

        public void SeparateInstrumental(
            string inputPath, string outputPath, string modelPath, int threads,
            Action<double, string>? progress, CancellationToken cancellationToken)
        {
            File.WriteAllText(outputPath, "instrumental");
            progress?.Invoke(1.0, "done");
        }
    }

    private sealed class FailingAiSeparator : IAiVocalSeparator
    {
        public bool IsSupported => true;
        public string BackendName => "failing-ai";

        public void SeparateInstrumental(
            string inputPath, string outputPath, string modelPath, int threads,
            Action<double, string>? progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("fake AI failure");
    }

    private sealed class FakeStereoSource : IAudioSource
    {
        private readonly float[] _samples;
        private int _position;

        public FakeStereoSource()
        {
            const int rate = 44100;
            _samples = new float[rate * 2];
            for (int i = 0; i < rate; i++)
            {
                double t = (double)i / rate;
                float value = (float)(0.15 * Math.Sin(2 * Math.PI * 440 * t));
                _samples[i * 2] = value;
                _samples[i * 2 + 1] = value;
            }
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime => TimeSpan.FromSeconds(1);
        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)_position / (WaveFormat.SampleRate * 2));
            set => _position = Math.Clamp((int)(value.TotalSeconds * WaveFormat.SampleRate * 2), 0, _samples.Length);
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

        public ISampleProvider ToSampleProvider() => new FakeSampleProvider(_samples);
        public void Dispose() { }
    }

    private sealed class FakeSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;
        public FakeSampleProvider(float[] samples)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
