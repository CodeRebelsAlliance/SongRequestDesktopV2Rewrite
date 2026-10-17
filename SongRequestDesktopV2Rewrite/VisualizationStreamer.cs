using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Streams down-sampled audio frames from the music player to the web
/// visualization window. Replaces the WPF <c>VisualizationWindow</c>'s direct
/// subscription to <see cref="MusicPlayer.AudioSamplesCaptured"/>.
///
/// Frames are emitted at ~30 fps as Base64-encoded signed 8-bit samples to keep
/// the IPC payload small.
/// </summary>
public sealed class VisualizationStreamer : IDisposable
{
    private const int FrameSamples = 1024;          // interleaved samples per emitted frame
    private const int MinIntervalMs = 33;           // ~30 fps
    private const int MaxPendingSamples = FrameSamples * 12;

    private readonly MusicEngine _engine;
    private readonly object _lock = new();
    private readonly List<float> _pending = new();
    private DateTime _lastEmit = DateTime.MinValue;
    private bool _disposed;

    public event Action<object>? FrameReady;

    public VisualizationStreamer(MusicEngine engine)
    {
        _engine = engine;
        _engine.AudioSamplesCaptured += OnSamplesCaptured;
    }

    internal void OnSamplesCaptured(object? sender, float[] samples)
    {
        if (_disposed || samples == null || samples.Length == 0) return;

        object? frame = null;
        lock (_lock)
        {
            _pending.AddRange(samples);

            // Bound memory if frames aren't being drained fast enough.
            if (_pending.Count > MaxPendingSamples)
                _pending.RemoveRange(0, _pending.Count - MaxPendingSamples);

            var now = DateTime.UtcNow;
            if ((now - _lastEmit).TotalMilliseconds < MinIntervalMs)
                return;
            _lastEmit = now;

            int take = Math.Min(FrameSamples, _pending.Count);
            if (take <= 0) return;

            var data = new byte[take];
            int start = _pending.Count - take;
            for (int i = 0; i < take; i++)
            {
                float v = _pending[start + i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                int iv = (int)Math.Round(v * 127f);
                data[i] = (byte)(iv & 0xFF);
            }
            _pending.Clear();

            frame = new
            {
                channels = _engine.CaptureChannels,
                sampleRate = _engine.CaptureSampleRate,
                sampleCount = take,
                data = Convert.ToBase64String(data)
            };
        }

        if (frame != null) FrameReady?.Invoke(frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _engine.AudioSamplesCaptured -= OnSamplesCaptured; } catch { }
    }
}
