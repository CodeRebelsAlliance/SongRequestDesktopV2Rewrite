using System;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Silk.NET.OpenAL;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform <see cref="IAudioOutput"/> backed by OpenAL Soft (via
/// Silk.NET). Samples are pulled from the supplied <see cref="ISampleProvider"/>,
/// converted to 16-bit and streamed through a small ring of OpenAL buffers.
///
/// Mirrors the semantics of the NAudio <c>WaveOutEvent</c> implementation:
/// a starved/ended source raises <see cref="PlaybackStopped"/>, and
/// <see cref="Play"/> resumes/re-primes the buffer ring.
/// </summary>
internal sealed unsafe class OpenAlAudioOutput : IAudioOutput
{
    private const int BufferCount = 4;
    private const int FramesPerBuffer = 4096;

    /// <summary>
    /// Serializes ALL OpenAL calls of every output in the process.
    ///
    /// <c>alcMakeContextCurrent</c> is <b>process-wide</b> with the OpenAL build we
    /// ship, not per-thread: while the announcement chime played, its context was
    /// current and the music's feed thread issued its own calls (with source/buffer
    /// ids from the music context) against the chime's context — every one of them
    /// failed with <c>AL_INVALID_NAME</c>, the music's buffers stopped being queued and
    /// the track "hung"/looped while the chime stayed broken. Each output therefore
    /// selects its context <i>inside</i> this gate, immediately before its calls.
    /// </summary>
    internal static readonly object AlGate = new();

    private readonly object _sync = new();
    private readonly AL _al;
    private readonly ALContext _alc;

    private Device* _device;
    private Context* _context;
    private int _currentDeviceNumber = -1;
    private uint _source;
    private readonly uint[] _bufferIds = new uint[BufferCount];
    private readonly uint[] _unqueueScratch = new uint[BufferCount];

    private ISampleProvider? _provider;
    private float[] _floatScratch = Array.Empty<float>();
    private short[] _shortScratch = Array.Empty<short>();
    private int _channels = 2;
    private int _sampleRate = 44100;

    private Thread? _thread;
    private volatile bool _running;
    private bool _started;
    private bool _stoppedRaised;
    private bool _disposed;
    private volatile bool _sourceExhausted;
    private int _deviceNumber = -1;
    private float _volume = 1f;
    private PlaybackState _state = PlaybackState.Stopped;

    public event EventHandler? PlaybackStopped;

    public OpenAlAudioOutput()
    {
        _alc = ALContext.GetApi();
        _al = AL.GetApi();

        OpenDevice(-1); // default device — validates OpenAL availability early
    }

    /// <summary>Closes any open device and opens the one for <paramref name="number"/> (-1 = default).</summary>
    private void OpenDevice(int number)
    {
        lock (AlGate)
        {
            if (_context != null)
            {
                try { EnsureCurrent(); } catch { }
                try { _alc.MakeContextCurrent(null); } catch { }
                try { _alc.DestroyContext(_context); } catch { }
                _context = null;
            }
            if (_device != null)
            {
                try { _alc.CloseDevice(_device); } catch { }
                _device = null;
            }

            var name = number >= 0 ? OpenAlDevices.NameAt(number) : null;

            _device = name != null ? _alc.OpenDevice(name) : _alc.OpenDevice(null);
            if (_device == null && name != null) _device = _alc.OpenDevice(null); // fall back to default
            if (_device == null) throw new InvalidOperationException("Unable to open an OpenAL device.");

            _context = _alc.CreateContext(_device, null);
            if (_context == null) throw new InvalidOperationException("Unable to create an OpenAL context.");

            _alc.MakeContextCurrent(_context);
            _currentDeviceNumber = name != null ? number : -1;
        }
    }

    public PlaybackState PlaybackState
    {
        get { lock (_sync) return _state; }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (AlGate)
            lock (_sync)
            {
                if (!_started || _disposed) return;
                EnsureCurrent();
                _al.SetSourceProperty(_source, SourceFloat.Gain, _volume);
            }
        }
    }

    public int DeviceNumber
    {
        get => _deviceNumber;
        // Applied when Init runs (the device is opened/reopened there).
        set => _deviceNumber = value;
    }

    public void Init(ISampleProvider source) => InitCore(source);

    public void Init(IWaveProvider source) => InitCore(source.ToSampleProvider());

    private void InitCore(ISampleProvider provider)
    {
        lock (AlGate)
        lock (_sync)
        {
            if (_started || _disposed) return;

            _provider = provider;
            _channels = provider.WaveFormat.Channels == 1 ? 1 : 2;
            _sampleRate = provider.WaveFormat.SampleRate;
            if (_sampleRate <= 0) _sampleRate = 44100;

            _floatScratch = new float[FramesPerBuffer * _channels];
            _shortScratch = new short[FramesPerBuffer * _channels];

            if (_currentDeviceNumber != _deviceNumber) OpenDevice(_deviceNumber);

            EnsureCurrent();
            fixed (uint* p = _bufferIds) _al.GenBuffers(BufferCount, p);
            fixed (uint* p = &_source) _al.GenSources(1, p);
            _al.SetSourceProperty(_source, SourceFloat.Gain, _volume);

            PrimeBuffers();

            _started = true;
            _state = PlaybackState.Stopped;

            _running = true;
            _thread = new Thread(FeedLoop) { IsBackground = true, Name = "openal-feed" };
            _thread.Start();
        }
    }

    private void PrimeBuffers()
    {
        for (int i = 0; i < BufferCount; i++)
        {
            int frames = FillBuffer(_bufferIds[i]);
            if (frames <= 0) break;
            EnsureCurrent();
            fixed (uint* p = &_bufferIds[i]) _al.SourceQueueBuffers(_source, 1, p);
        }
    }

    public void Play()
    {
        lock (AlGate)
        lock (_sync)
        {
            if (!_started || _disposed) return;
            _stoppedRaised = false;
            _sourceExhausted = false;
            _state = PlaybackState.Playing;
            EnsureCurrent();

            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued == 0) PrimeBuffers();

            EnsureSourcePlaying();
        }
    }

    public void Pause()
    {
        lock (AlGate)
        lock (_sync)
        {
            if (!_started || _disposed) return;
            EnsureCurrent();
            _al.SourcePause(_source);
            if (_state == PlaybackState.Playing) _state = PlaybackState.Paused;
        }
    }

    public void Stop()
    {
        lock (AlGate)
        lock (_sync)
        {
            if (!_started || _disposed) return;
            EnsureCurrent();
            _al.SourceStop(_source);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued > 0)
            {
                int n = Math.Min(queued, BufferCount);
                fixed (uint* p = _unqueueScratch) _al.SourceUnqueueBuffers(_source, n, p);
            }
            _state = PlaybackState.Stopped;
        }
        RaiseStopped();
    }

    private void FeedLoop()
    {
        lock (AlGate) { try { _alc.MakeContextCurrent(_context); } catch { } }

        while (_running)
        {
            bool starved = false;
            try { starved = RefillOnce(); }
            catch { starved = true; }

            if (starved)
            {
                if (!_stoppedRaised)
                {
                    _stoppedRaised = true;
                    lock (_sync) { if (_state == PlaybackState.Playing) _state = PlaybackState.Stopped; }
                    RaiseStopped();
                }
                Thread.Sleep(20);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>Refills any processed buffers. Returns true if the source had no data (starved/ended).</summary>
    private bool RefillOnce()
    {
        lock (AlGate)
        lock (_sync)
        {
            if (!_started || _disposed) return false;
            EnsureCurrent();

            EnsureCurrent();
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            if (processed == 0)
            {
                if (_state == PlaybackState.Playing)
                {
                    // The source may have stopped with nothing left to process (the
                    // processed count can read 0 once it drains). If the provider is
                    // exhausted and the source is no longer playing, we are done.
                    _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                    if (_sourceExhausted && (SourceState)state != SourceState.Playing) return true;

                    EnsureSourcePlaying();
                }
                return false;
            }

            bool starved = false;
            bool queuedAny = false;
            for (int i = 0; i < processed; i++)
            {
                uint id;
                _al.SourceUnqueueBuffers(_source, 1, &id);
                int frames = FillBuffer(id);
                if (frames > 0)
                {
                    EnsureCurrent();
                    _al.SourceQueueBuffers(_source, 1, &id);
                    queuedAny = true;
                }
                else
                {
                    starved = true;
                }
            }

            if (queuedAny && _state == PlaybackState.Playing) EnsureSourcePlaying();
            return starved;
        }
    }

    private void EnsureSourcePlaying()
    {
        EnsureCurrent();
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int st);
        if ((SourceState)st != SourceState.Playing) _al.SourcePlay(_source);
    }

    /// <summary>Reads up to one buffer of samples and uploads them to OpenAL. Returns frames written.</summary>
    private int FillBuffer(uint bufferId)
    {
        var provider = _provider;
        if (provider == null) return 0;

        int sampleCount = FramesPerBuffer * _channels;
        int read = 0;
        while (read < sampleCount)
        {
            int r = provider.Read(_floatScratch, read, sampleCount - read);
            if (r <= 0) break;
            read += r;
        }
        if (read <= 0)
        {
            _sourceExhausted = true;
            return 0;
        }

        for (int i = 0; i < read; i++)
        {
            float v = _floatScratch[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            _shortScratch[i] = (short)(v * 32767f);
        }

        int frames = read / _channels;
        if (frames <= 0) return 0;

        _sourceExhausted = false;

        // The provider read above runs arbitrary code (decoding), which can take long
        // enough for another output's thread to make its own context current. Never
        // assume the context is still ours: re-select right before the call.
        EnsureCurrent();

        fixed (short* p = _shortScratch)
        {
            _al.BufferData(bufferId, _channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16, p, frames * _channels * 2, _sampleRate);
        }
        return frames;
    }

    private void EnsureCurrent()
    {
        if (_alc.GetCurrentContext() != _context)
            _alc.MakeContextCurrent(_context);
    }

    private void RaiseStopped()
    {
        var handler = PlaybackStopped;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ => handler(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _running = false;
        try { _thread?.Join(750); } catch { }   // must be outside the gate: the feed thread needs it

        lock (AlGate)
        lock (_sync)
        {
            try
            {
                if (_started)
                {
                    EnsureCurrent();
                    fixed (uint* p = &_source) _al.DeleteSources(1, p);
                    fixed (uint* p = _bufferIds) _al.DeleteBuffers(BufferCount, p);
                    _started = false;
                }
            }
            catch { }

            // Never leave a destroyed context as the process-wide current one.
            try { if (_context != null && _alc.GetCurrentContext() == _context) _alc.MakeContextCurrent(null); } catch { }
            try { if (_context != null) _alc.DestroyContext(_context); } catch { }
            try { if (_device != null) _alc.CloseDevice(_device); } catch { }
            _context = null;
            _device = null;
        }
    }
}
