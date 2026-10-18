using System;
using System.Collections.Generic;
using Android.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Android <see cref="IAudioSource"/>: the platform's own demuxer + decoder
/// (<c>MediaExtractor</c> + <c>MediaCodec</c>) turned into a float32 sample
/// provider, which is what the engine's mixer, crossfade, loudness scan and
/// visualisation tap consume.
///
/// The exposed <see cref="WaveFormat"/> is the *track* format (cheap to read, so
/// library scans stay fast). Should the decoder emit a different rate/channel
/// layout (HE-AAC/SBR doubles the rate), the read path resamples internally, so
/// the engine always sees one stable format per source.
/// </summary>
internal sealed class AndroidAudioSource : IAudioSource
{
    /// <summary>Decoder dequeue timeout while the queue is empty (µs).</summary>
    private const long DrainTimeoutUs = 5_000;

    private readonly object _sync = new();
    private readonly string _path;

    private MediaExtractor? _extractor;
    private MediaCodec? _codec;
    private MediaFormat? _trackFormat;

    private readonly int _exposedRate;
    private readonly int _exposedChannels;
    private readonly TimeSpan _duration;

    private int _decoderRate;
    private int _decoderChannels;
    private bool _decoderFloat;

    private readonly Queue<float[]> _queue = new();
    private float[]? _current;
    private int _currentOffset;

    private bool _started;
    private bool _inputDone;
    private bool _outputDone;
    private bool _disposed;

    // Position is tracked in *decoder* frames: playback reads through the
    // provider chain (resampler / mono->stereo), so counting bytes in the public
    // Read() would never see those reads. Frames are rate-independent and are
    // converted to the exposed format only when reporting.
    private long _seekBaseFrames;
    private long _framesConsumed;
    private long _decodedSamples;
    private int _decodeFailures;
    private float _volume = 1f;

    private ISampleProvider? _normalized;
    private RawProvider? _raw;

    public AndroidAudioSource(string path)
    {
        _path = path;

        _extractor = new MediaExtractor();
        _extractor.SetDataSource(path);

        for (int i = 0; i < _extractor.TrackCount; i++)
        {
            var format = _extractor.GetTrackFormat(i);
            var mime = format?.GetString(MediaFormat.KeyMime);
            if (mime != null && mime.StartsWith("audio/", StringComparison.Ordinal))
            {
                _trackFormat = format;
                _extractor.SelectTrack(i);
                break;
            }
        }

        if (_trackFormat == null)
        {
            _extractor.Release();
            _extractor = null;
            throw new InvalidOperationException($"No audio track in '{path}'.");
        }

        _exposedRate = GetInt(_trackFormat, MediaFormat.KeySampleRate, 44100);
        _exposedChannels = GetInt(_trackFormat, MediaFormat.KeyChannelCount, 2);
        _decoderRate = _exposedRate;
        _decoderChannels = _exposedChannels;

        var durationUs = GetLong(_trackFormat, MediaFormat.KeyDuration, 0);
        _duration = durationUs > 0 ? TimeSpan.FromMilliseconds(durationUs / 1000.0) : TimeSpan.Zero;
        if (_duration <= TimeSpan.Zero)
        {
            // Some containers omit KEY_DURATION; the tags usually know, and the
            // engine's end-of-track tick needs a real length.
            _duration = FFmpegProbe.TryDurationFromTagLib(path);
            Android.Util.Log.Debug("SongRequest", $"duration from tags: {_duration.TotalSeconds:F2}s for {System.IO.Path.GetFileName(path)}");
        }
        Android.Util.Log.Debug("SongRequest", $"source: {System.IO.Path.GetFileName(path)} {_exposedRate}Hz {_exposedChannels}ch {_duration.TotalSeconds:F2}s mime={_trackFormat.GetString(MediaFormat.KeyMime)}");

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_exposedRate, _exposedChannels);
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan TotalTime => _duration;

    public long Length => _duration > TimeSpan.Zero
        ? (long)(_duration.TotalSeconds * _exposedRate * _exposedChannels * 4)
        : 0;

    public TimeSpan CurrentTime
    {
        get { lock (_sync) return FramesToTime(_seekBaseFrames + _framesConsumed); }
        set => SeekToBytes(TimeToBytes(value));
    }

    /// <summary>Position in exposed-format bytes (looping and the loudness scan use it).</summary>
    public long Position
    {
        get { lock (_sync) return (long)(CurrentTime.TotalSeconds * _exposedRate * _exposedChannels * 4); }
        set => SeekToBytes(value);
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Max(0f, value);
    }

    public ISampleProvider ToSampleProvider()
    {
        lock (_sync) return _normalized ??= BuildChain();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        lock (_sync)
        {
            if (_disposed) return 0;

            var normalized = _normalized ??= BuildChain();
            return normalized.Read(buffer, offset, count);
        }
    }

    public void Dispose()
    {
        Android.Util.Log.Debug("SongRequest", $"source dispose: {System.IO.Path.GetFileName(_path)} decoded={_decodedSamples} samples, pos={CurrentTime.TotalSeconds:F1}s");
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Clear();
            _current = null;
            _normalized = null;
            _raw = null;
        }

        try { _codec?.Stop(); } catch { }
        try { _codec?.Release(); } catch { }
        try { _extractor?.Release(); } catch { }
        _codec = null;
        _extractor = null;
    }

    // ── Decoder plumbing ────────────────────────────────────────────────────

    private void EnsureStarted()
    {
        if (_started) return;

        var mime = _trackFormat!.GetString(MediaFormat.KeyMime)
                   ?? throw new InvalidOperationException("The audio track has no MIME type.");

        _codec = MediaCodec.CreateDecoderByType(mime);
        _codec.Configure(_trackFormat, null, null, MediaCodecConfigFlags.None);
        _codec.Start();
        _started = true;
    }

    /// <summary>
    /// Builds the provider chain, waiting for the decoder's real output format so
    /// the conversion (if any) is correct from the first sample.
    /// </summary>
    private ISampleProvider BuildChain()
    {
        EnsureStarted();

        // Give the decoder a moment to report its output format.
        for (int i = 0; i < 200 && _queue.Count == 0; i++)
        {
            if (_inputDone && _outputDone) break;
            Pump(blocking: true);
        }

        _raw = new RawProvider(this);
        ISampleProvider sp = _raw;

        if (_decoderChannels == 1 && _exposedChannels == 2)
            sp = new MonoToStereoSampleProvider(sp);

        if (_decoderRate != _exposedRate)
            sp = new WdlResamplingSampleProvider(sp, _exposedRate);

        return sp;
    }

    /// <summary>Runs one decode step. Returns false once nothing is left.</summary>
    private bool Pump(bool blocking)
    {
        var codec = _codec;
        var extractor = _extractor;
        if (codec == null || extractor == null) return false;

        var info = new MediaCodec.BufferInfo();
        int outIndex = codec.DequeueOutputBuffer(info, blocking ? DrainTimeoutUs : 0);

        if (outIndex >= 0)
        {
            try
            {
                if (info.Size > 0)
                {
                    var buffer = codec.GetOutputBuffer(outIndex);
                    if (buffer != null)
                    {
                        buffer.Position(info.Offset);
                        var samples = ToFloat(buffer, info.Size);
                        if (samples.Length > 0) { _queue.Enqueue(samples); _decodedSamples += samples.Length; }
                    }
                }

                _decodeFailures = 0;

                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                    _outputDone = true;
            }
            catch (Exception ex)
            {
                // A buffer can be invalidated by a concurrent Flush (seek). That is
                // not end-of-stream: drop the buffer and keep going, but give up if
                // the decoder keeps failing (a genuinely broken stream must not spin).
                _decodeFailures++;
                Android.Util.Log.Debug("SongRequest", $"Decode failure #{_decodeFailures}: {ex.Message}");
                if (_decodeFailures > 20) _outputDone = true;
            }
            finally
            {
                try { codec.ReleaseOutputBuffer(outIndex, false); } catch { }
            }

            return !(_inputDone && _outputDone && _queue.Count == 0);
        }

        if (outIndex == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            UpdateDecoderFormat(codec.OutputFormat);
            Android.Util.Log.Debug("SongRequest", $"decoder output format: {_decoderRate}Hz {_decoderChannels}ch float={_decoderFloat}");
            return true;
        }

        if (outIndex == (int)MediaCodecInfoState.OutputBuffersChanged) return true;

        // Nothing to drain: feed the decoder.
        if (!_inputDone)
        {
            int inIndex = codec.DequeueInputBuffer(blocking ? DrainTimeoutUs : 0);
            if (inIndex >= 0)
            {
                var input = codec.GetInputBuffer(inIndex);
                if (input != null)
                {
                    input.Clear();
                    int size = extractor.ReadSampleData(input, 0);
                    if (size < 0)
                    {
                        codec.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                        _inputDone = true;
                    }
                    else
                    {
                        codec.QueueInputBuffer(inIndex, 0, size, extractor.SampleTime, MediaCodecBufferFlags.None);
                        extractor.Advance();
                    }
                }
            }
        }

        return !(_inputDone && _outputDone && _queue.Count == 0);
    }

    private void UpdateDecoderFormat(MediaFormat? format)
    {
        if (format == null) return;

        var rate = GetInt(format, MediaFormat.KeySampleRate, _decoderRate);
        var channels = GetInt(format, MediaFormat.KeyChannelCount, _decoderChannels);
        if (rate <= 0) rate = _decoderRate;
        if (channels <= 0) channels = _decoderChannels;

        var encoding = GetInt(format, MediaFormat.KeyPcmEncoding, (int)Encoding.Pcm16bit);

        if (rate == _decoderRate && channels == _decoderChannels && encoding == (int)Encoding.Pcm16bit == !_decoderFloat)
            return;

        _decoderRate = rate;
        _decoderChannels = channels;
        _decoderFloat = encoding == (int)Encoding.PcmFloat;

        // The chain (and its resampler) depends on the decoder format: rebuild it.
        _normalized = null;
        _raw = null;
    }

    private float[] ToFloat(Java.Nio.ByteBuffer buffer, int size)
    {
        if (_decoderFloat)
        {
            int count = size / 4;
            if (count <= 0) return Array.Empty<float>();

            var floats = new float[count];
            var view = buffer.AsFloatBuffer();
            view.Get(floats, 0, count);
            return floats;
        }

        int shorts = size / 2;
        if (shorts <= 0) return Array.Empty<float>();

        var raw = new short[shorts];
        buffer.AsShortBuffer().Get(raw, 0, shorts);

        var result = new float[shorts];
        for (int i = 0; i < shorts; i++)
            result[i] = raw[i] / 32768f;
        return result;
    }

    /// <summary>Pulls decoder-format samples (used by the provider chain).</summary>
    private int ReadDecoder(float[] buffer, int offset, int count)
    {
        int written = 0;

        while (written < count)
        {
            if (_current == null || _currentOffset >= _current.Length)
            {
                if (!NextChunk()) break;
            }

            var chunk = _current!;
            int take = Math.Min(count - written, chunk.Length - _currentOffset);
            Array.Copy(chunk, _currentOffset, buffer, offset + written, take);
            _currentOffset += take;
            written += take;
        }

        if (written > 0)
        {
            var channels = _decoderChannels > 0 ? _decoderChannels : 1;
            _framesConsumed += written / channels;

            if (_volume != 1f)
            {
                for (int i = 0; i < written; i++)
                    buffer[offset + i] *= _volume;
            }
        }

        return written;
    }

    private bool NextChunk()
    {
        while (_queue.Count == 0)
        {
            if (_inputDone && _outputDone) return false;
            if (!Pump(blocking: true))
            {
                if (_queue.Count == 0) return false;
            }
        }

        _current = _queue.Dequeue();
        _currentOffset = 0;
        return true;
    }

    private void SeekToBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;

        lock (_sync)
        {
            if (_disposed) return;

            var target = TimeSpan.FromSeconds(bytes / (double)(_exposedRate * _exposedChannels * 4));
            EnsureStarted();

            try
            {
                _extractor!.SeekTo((long)(target.TotalSeconds * 1_000_000), MediaExtractorSeekTo.ClosestSync);
                _codec!.Flush();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"Seek failed: {ex.Message}");
            }

            _queue.Clear();
            _current = null;
            _currentOffset = 0;
            _inputDone = false;
            _outputDone = false;
            _decodeFailures = 0;
            _seekBaseFrames = (long)(target.TotalSeconds * (_decoderRate > 0 ? _decoderRate : _exposedRate));
            _framesConsumed = 0;
            _normalized = null;
            _raw = null;
        }
    }

    private long TimeToBytes(TimeSpan time)
        => (long)(Math.Max(0, time.TotalSeconds) * _exposedRate * _exposedChannels * 4);

    private TimeSpan FramesToTime(long frames)
    {
        var rate = _decoderRate > 0 ? _decoderRate : _exposedRate;
        return rate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(frames / (double)rate);
    }

    private static int GetInt(MediaFormat format, string key, int fallback)
    {
        try { return format.ContainsKey(key) ? format.GetInteger(key) : fallback; }
        catch { return fallback; }
    }

    private static long GetLong(MediaFormat format, string key, long fallback)
    {
        try { return format.ContainsKey(key) ? format.GetLong(key) : fallback; }
        catch { return fallback; }
    }

    /// <summary>Decoder-format sample provider; the chain wraps it when needed.</summary>
    private sealed class RawProvider : ISampleProvider
    {
        private readonly AndroidAudioSource _owner;

        public RawProvider(AndroidAudioSource owner)
        {
            _owner = owner;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(owner._decoderRate, owner._decoderChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) => _owner.ReadDecoder(buffer, offset, count);
    }
}
