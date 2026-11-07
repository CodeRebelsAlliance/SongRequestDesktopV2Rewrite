using System;
using System.Collections.Generic;
using System.Threading;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Taps the engine's captured audio for the visualization window.
    /// Frames are emitted at ~30 fps as Base64-encoded signed 8-bit samples to
    /// keep the IPC payload small.
    ///
    /// The audio callback only copies samples into a bounded queue. Building the
    /// frame (quantization, Base64, JSON later) and invoking the UI bridge runs on
    /// a timer thread. Doing that work inline used to block the native audio
    /// callback, which could stall or crash the Linux/OpenAL output when playback
    /// started with the visualization already open.
    /// </summary>
    public sealed class VisualizationStreamer : IDisposable
    {
        private const int FrameSamples = 1024;          // interleaved samples per emitted frame
        private const int MinIntervalMs = 33;           // ~30 fps
        private const int MaxPendingSamples = FrameSamples * 12;

        private readonly MusicEngine _engine;
        private readonly object _lock = new();
        private readonly List<float> _pending = new();
        private readonly Timer? _pump;
        private DateTime _lastEmit = DateTime.MinValue;
        private volatile bool _disposed;
        private int _pumpBusy;

        public event Action<object>? FrameReady;

        public VisualizationStreamer(MusicEngine engine)
            : this(engine, autoPump: true)
        {
        }

        /// <summary>Used by tests to drive frames with <see cref="DrainFrame"/>.</summary>
        internal VisualizationStreamer(MusicEngine engine, bool autoPump)
        {
            _engine = engine;
            _engine.AudioSamplesCaptured += OnSamplesCaptured;

            if (autoPump)
            {
                _pump = new Timer(_ =>
                {
                    try { DrainFrame(); } catch { /* never kill the process */ }
                }, null, MinIntervalMs, MinIntervalMs);
            }
        }

        /// <summary>
        /// Called by the audio callback. Keep this allocation-light and never do
        /// UI/IPC work here.
        /// </summary>
        internal void OnSamplesCaptured(object? sender, float[] samples)
        {
            if (_disposed || samples == null || samples.Length == 0) return;

            lock (_lock)
            {
                _pending.AddRange(samples);

                // Bound memory if the pump or the UI is slower than the audio.
                if (_pending.Count > MaxPendingSamples)
                    _pending.RemoveRange(0, _pending.Count - MaxPendingSamples);
            }
        }

        /// <summary>Builds and emits one frame if enough samples and time elapsed.</summary>
        internal void DrainFrame()
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _pumpBusy, 1, 0) != 0) return;

            try
            {
                object? frame = null;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastEmit).TotalMilliseconds < MinIntervalMs) return;
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

                if (frame != null && !_disposed)
                {
                    try { FrameReady?.Invoke(frame); }
                    catch
                    {
                        // A visualization/UI failure must never escape into the
                        // timer thread (or, previously, the audio callback).
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _pumpBusy, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _pump?.Dispose(); } catch { }
            try { _engine.AudioSamplesCaptured -= OnSamplesCaptured; } catch { }
        }
    }
}
