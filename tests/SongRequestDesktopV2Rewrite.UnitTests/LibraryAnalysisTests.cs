using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class LibraryAnalysisTests
{
    [Fact]
    public async Task Start_AnalyzesLibraryInBackground_AndRemoveSongDeletesItsCache()
    {
        TestSupport.ResetLibrary();
        var dir = TestSupport.NewTempDir("library-analysis");
        var file = Path.Combine(dir, "song.wav");
        File.WriteAllBytes(file, new byte[512]);

        var library = new LibraryService();
        library.Data.Songs.Clear();
        var song = new LibrarySong
        {
            Id = "analysis-1",
            Title = "Library Song",
            Artist = "Tester",
            FilePath = file,
            FileStatus = FileStatus.Present,
            Duration = TimeSpan.FromSeconds(6)
        };
        library.Data.Songs.Add(song);
        library.Data.AddToIndex(song);

        var cachePath = Path.Combine(dir, "analysis-cache.json");
        var analysis = new SongAnalysisService(_ => new BufferSource(5), cachePath);
        var service = new LibraryAnalysisService(library, analysis) { DelayBetweenSongsMs = 0 };

        var completed = new TaskCompletionSource<LibraryAnalysisProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ProgressChanged += progress =>
        {
            if (!progress.IsRunning && progress.Total > 0 && progress.Completed >= progress.Total)
                completed.TrySetResult(progress);
        };

        service.Start();
        var done = await Task.WhenAny(completed.Task, Task.Delay(15000));
        Assert.Same(completed.Task, done);
        var status = await completed.Task;

        Assert.Equal(1, status.Analyzed);
        Assert.Equal(0, status.Failed);
        Assert.NotNull(analysis.GetFresh(file));
        Assert.Equal(SongAnalyzer.CurrentAlgorithmVersion, analysis.GetFresh(file)!.AlgorithmVersion);

        // Running again skips the now-current analysis.
        var skipped = new TaskCompletionSource<LibraryAnalysisProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ProgressChanged += progress =>
        {
            if (!progress.IsRunning && progress.Total == 0) skipped.TrySetResult(progress);
        };
        service.Start();
        var skippedDone = await Task.WhenAny(skipped.Task, Task.Delay(5000));
        Assert.Same(skipped.Task, skippedDone);

        service.RemoveSong(song);
        Assert.Null(analysis.GetFresh(file));
    }

    [Fact]
    public async Task Cache_InvalidatesOldAlgorithmVersions_AndCanBeDeleted()
    {
        var dir = TestSupport.NewTempDir("analysis-version");
        var file = Path.Combine(dir, "song.wav");
        File.WriteAllBytes(file, new byte[512]);
        var service = new SongAnalysisService(_ => new BufferSource(4), Path.Combine(dir, "cache.json"));

        var fresh = await service.AnalyzeAsync(file, "Song", "Artist");
        Assert.True(service.GetFresh(file) != null);
        Assert.False(service.IsOutdated(file));

        fresh.AlgorithmVersion = "1.0";
        Assert.Null(service.GetFresh(file));
        Assert.True(service.IsOutdated(file));

        Assert.True(service.DeleteAnalysis(file));
        Assert.Null(service.GetCached(file));
    }

    [Fact]
    public async Task LyricsRefinementRace_IsAppliedBeforeTheFirstCachedResultIsReturned()
    {
        var dir = TestSupport.NewTempDir("analysis-lyrics-race");
        var file = Path.Combine(dir, "song.wav");
        File.WriteAllBytes(file, new byte[512]);
        var blocking = new BlockingSource(20);
        var service = new SongAnalysisService(_ => blocking, Path.Combine(dir, "cache.json"));

        var lines = new List<TimedLyricLine>();
        for (int i = 0; i < 12; i++)
            lines.Add(new TimedLyricLine(3 + i * 1.2, i % 4 < 2 ? "a unique verse line" : "the same chorus line"));

        // Start decoding, then deliver lyrics *after* the analyzer has begun and
        // before it caches its result. This is the first-playback race that used
        // to make the first result worse than the second (cached) one.
        var task = service.AnalyzeAsync(file, "Song", "Artist");
        Assert.True(blocking.Started.Wait(5000), "analyzer did not start");
        service.SetTimedLyrics(file, lines);
        blocking.Release.Set();

        var analysis = await task;
        Assert.True(analysis.LyricsUsedForParts);
    }

    private sealed class BlockingSource : IAudioSource, ISampleProvider
    {
        private readonly BufferSource _inner;
        private int _reads;

        public BlockingSource(int seconds) => _inner = new BufferSource(seconds);

        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public WaveFormat WaveFormat => _inner.WaveFormat;
        public TimeSpan TotalTime => _inner.TotalTime;
        public TimeSpan CurrentTime { get => _inner.CurrentTime; set => _inner.CurrentTime = value; }
        public long Length => _inner.Length;
        public long Position { get => _inner.Position; set => _inner.Position = value; }
        public float Volume { get => _inner.Volume; set => _inner.Volume = value; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (Interlocked.Exchange(ref _reads, 1) == 0)
            {
                Started.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return _inner.Read(buffer, offset, count);
        }

        public ISampleProvider ToSampleProvider() => this;
        public void Dispose() => _inner.Dispose();
    }

    private sealed class BufferSource : IAudioSource, ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public BufferSource(int seconds)
        {
            const int sampleRate = 44100;
            _samples = new float[sampleRate * seconds];
            for (int i = 0; i < _samples.Length; i++)
                _samples[i] = (float)(Math.Sin(2 * Math.PI * 220 * i / sampleRate) * 0.3);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_samples.Length / WaveFormat.SampleRate);
        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds((double)_position / WaveFormat.SampleRate);
            set => _position = Math.Clamp((int)(value.TotalSeconds * WaveFormat.SampleRate), 0, _samples.Length);
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
