using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform <see cref="IAudioSource"/> that decodes any container/codec
/// (MP3, AAC/MP4, Ogg, FLAC, WAV, and the Opus-in-WebM files YouTube downloads
/// are often saved as, mislabeled as ".mp3") via a bundled FFmpeg process that
/// streams raw 32-bit little-endian float PCM.
///
/// The decode process is started lazily on the first read, so duration-only
/// consumers never spawn a decoder. Seeking backwards (or to an arbitrary
/// position) restarts FFmpeg with <c>-ss</c>; seeking forwards skips samples
/// in-buffer, which keeps the loudness-sampling scan cheap.
/// </summary>
internal sealed class FFmpegAudioSource : IAudioSource, ISampleProvider
{
    private const int ChunkSize = 1 << 16; // 64 KiB

    private readonly string _path;
    private readonly string _ffmpegPath;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly long _bytesPerSecond;
    private readonly TimeSpan _totalTime;
    private readonly long _totalBytes;

    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private readonly SemaphoreSlim _chunkSignal = new(0);
    private readonly object _readLock = new();
    private readonly object _stateLock = new();

    private Process? _process;
    private Thread? _readerThread;
    private int _generation;
    private bool _decodeStarted;
    private volatile bool _producerDone;
    private volatile bool _disposed;

    // Consumer-side cursor (only touched while holding _readLock).
    private byte[]? _current;
    private int _currentOffset;

    // Progress accounting (guarded by _stateLock).
    private long _bytesConsumed;
    private long _seekBaseBytes;

    private float _volume = 1f;

    public FFmpegAudioSource(string path, string ffmpegPath, string? ffprobePath)
    {
        _path = path;
        _ffmpegPath = ffmpegPath;

        var info = FFmpegProbe.Probe(ffprobePath, path);
        _sampleRate = info.HasValue && info.Value.SampleRate > 0 ? info.Value.SampleRate : 44100;
        _channels = info.HasValue && info.Value.Channels > 0 ? info.Value.Channels : 2;
        _bytesPerSecond = (long)_sampleRate * _channels * 4;

        var duration = info?.Duration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
            duration = FFmpegProbe.TryDurationFromTagLib(path);
        _totalTime = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        _totalBytes = _totalTime > TimeSpan.Zero ? (long)(_totalTime.TotalSeconds * _bytesPerSecond) : 0;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan TotalTime => _totalTime;

    public long Length => _totalBytes;

    public float Volume
    {
        get => _volume;
        set => _volume = value;
    }

    public long Position
    {
        get { lock (_stateLock) return _seekBaseBytes + _bytesConsumed; }
        set => SeekToByte(value);
    }

    public TimeSpan CurrentTime
    {
        get => BytesToTime(Position);
        set => SeekToByte(TimeToBytes(value));
    }

    public ISampleProvider ToSampleProvider() => this;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || count <= 0) return 0;
        lock (_readLock)
        {
            return ReadCore(buffer, offset, count);
        }
    }

    private int ReadCore(float[] buffer, int offset, int count)
    {
        EnsureStarted();

        int floatsRead = 0;
        while (floatsRead < count)
        {
            if (_current == null || _currentOffset >= _current.Length)
            {
                if (!WaitForChunk(out _current))
                {
                    _current = null;
                    break;
                }
                _currentOffset = 0;
            }

            if (_current == null) break;

            int floatsAvailable = (_current.Length - _currentOffset) / 4;
            if (floatsAvailable <= 0)
            {
                _current = null;
                continue;
            }

            int toCopy = Math.Min(floatsAvailable, count - floatsRead);
            Buffer.BlockCopy(_current, _currentOffset, buffer, (offset + floatsRead) * 4, toCopy * 4);
            _currentOffset += toCopy * 4;
            floatsRead += toCopy;
        }

        if (floatsRead > 0)
        {
            lock (_stateLock) _bytesConsumed += (long)floatsRead * 4;

            if (_volume != 1f)
            {
                for (int i = 0; i < floatsRead; i++) buffer[offset + i] *= _volume;
            }
        }

        return floatsRead;
    }

    private void EnsureStarted()
    {
        if (_decodeStarted) return;
        if (_disposed) return;
        _decodeStarted = true;
        StartProcess(BytesToTime(_seekBaseBytes));
    }

    private bool WaitForChunk(out byte[]? chunk)
    {
        while (true)
        {
            if (_chunks.TryDequeue(out chunk)) return true;
            if (_disposed || (_producerDone && _chunks.IsEmpty))
            {
                chunk = null;
                return false;
            }
            _chunkSignal.Wait(250);
        }
    }

    private void StartProcess(TimeSpan start)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Arg(string a) => psi.ArgumentList.Add(a);

        Arg("-hide_banner");
        Arg("-loglevel"); Arg("error");
        if (start > TimeSpan.Zero)
        {
            Arg("-ss");
            Arg(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        Arg("-i"); Arg(_path);
        Arg("-vn"); Arg("-sn"); Arg("-dn");
        Arg("-map"); Arg("0:a:0");
        Arg("-f"); Arg("f32le");
        Arg("-acodec"); Arg("pcm_f32le");
        Arg("-ar"); Arg(_sampleRate.ToString(CultureInfo.InvariantCulture));
        Arg("-ac"); Arg(_channels.ToString(CultureInfo.InvariantCulture));
        Arg("pipe:1");

        int generation = ++_generation;
        _producerDone = false;
        _current = null;
        _currentOffset = 0;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) System.Diagnostics.Debug.WriteLine($"[ffmpeg] {e.Data}");
        };
        process.BeginErrorReadLine();

        _process = process;

        var thread = new Thread(() => ReaderLoop(generation, process))
        {
            IsBackground = true,
            Name = "ffmpeg-decode"
        };
        _readerThread = thread;
        thread.Start();
    }

    private void ReaderLoop(int generation, Process process)
    {
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
            {
                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                _chunks.Enqueue(copy);
                _chunkSignal.Release();
            }
        }
        catch
        {
            // Process killed on seek/dispose, or a read error.
        }
        finally
        {
            if (generation == _generation)
            {
                _producerDone = true;
                _chunkSignal.Release();
            }
        }
    }

    private void Restart(TimeSpan start)
    {
        StopProcess();
        _decodeStarted = true;
        StartProcess(start);
    }

    private void StopProcess()
    {
        var process = _process;
        var thread = _readerThread;
        _process = null;
        _readerThread = null;

        _producerDone = true; // unblock any waiting reader

        try { process?.Kill(true); } catch { }
        try { thread?.Join(750); } catch { }
        try { process?.Dispose(); } catch { }

        while (_chunks.TryDequeue(out _)) { }
        while (_chunkSignal.Wait(0)) { }

        _current = null;
        _currentOffset = 0;
    }

    private void SeekToByte(long target)
    {
        if (target < 0) target = 0;
        if (_totalBytes > 0 && target > _totalBytes) target = _totalBytes;

        lock (_readLock)
        {
            long current;
            lock (_stateLock) current = _seekBaseBytes + _bytesConsumed;

            if (!_decodeStarted)
            {
                lock (_stateLock) { _seekBaseBytes = target; _bytesConsumed = 0; }
                return;
            }

            if (target < current)
            {
                lock (_stateLock) { _seekBaseBytes = target; _bytesConsumed = 0; }
                Restart(BytesToTime(target));
                return;
            }

            long remaining = target - current;
            var scratch = new float[8192];
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining / 4, scratch.Length);
                if (want <= 0) break;
                int r = ReadCore(scratch, 0, want);
                if (r <= 0) break;
                remaining -= (long)r * 4;
            }
        }
    }

    private long TimeToBytes(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return 0;
        return (long)(time.TotalSeconds * _bytesPerSecond);
    }

    private TimeSpan BytesToTime(long bytes)
    {
        if (bytes <= 0 || _bytesPerSecond <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_readLock)
        {
            StopProcess();
        }
    }
}
