using System;
using System.Threading;
using Android.Content;
using Android.Media;
using NAudio.Wave;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Android <see cref="IAudioOutput"/> backed by a streaming <c>AudioTrack</c>.
///
/// The engine hands us its mixer (44.1 kHz stereo float) and we pull from it on a
/// dedicated render thread, writing straight into the track — the same shape the
/// desktop's OpenAL output has, so the crossfade mixer, normalisation, ducking and
/// the visualisation tap keep working unchanged.
///
/// Audio focus is handled here (pause on loss, resume on regain) and output
/// routing uses <c>SetPreferredDevice</c>, which is what the player's device menu
/// drives through <see cref="IAudioOutput.DeviceNumber"/>.
/// </summary>
internal sealed class AndroidAudioOutput : IAudioOutput
{
    /// <summary>How many times the device minimum buffer to use (latency vs. safety).</summary>
    private const int BufferMultiplier = 3;

    /// <summary>
    /// Largest chunk pulled from the source per write (samples, not frames).
    ///
    /// The engine's visualisation tap fires once per read, so this also sets the
    /// visualisation's ceiling: 8192 samples = 93 ms per read capped the spectrum at
    /// ~10 fps on device. 1024 samples (~12 ms) leaves the 30 fps target plenty of
    /// headroom, and AudioTrack is happy with the smaller writes (the device buffer
    /// is still several of them deep).
    /// </summary>
    private const int ChunkSamples = 1024;

    private readonly object _sync = new();
    private readonly AudioManager? _audioManager;

    private AudioTrack? _track;
    private Thread? _renderThread;
    private ISampleProvider? _source;
    private float[] _buffer = Array.Empty<float>();
    private short[]? _shortBuffer;

    private volatile bool _running;
    private volatile bool _paused;
    private bool _useFloat = true;
    private float _volume = 1f;
    private int _deviceNumber = -1;
    private NAudio.Wave.PlaybackState _state = NAudio.Wave.PlaybackState.Stopped;

    private long _samplesWritten;
    private AudioFocusRequestClass? _focusRequest;
    private bool _resumeOnFocusGain;

    public AndroidAudioOutput()
    {
        try { _audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService); }
        catch { _audioManager = null; }
    }

    public NAudio.Wave.PlaybackState PlaybackState
    {
        get { lock (_sync) return _state; }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            try { _track?.SetVolume(_volume); } catch { }
        }
    }

    public int DeviceNumber
    {
        get => _deviceNumber;
        set
        {
            if (_deviceNumber == value) return;
            _deviceNumber = value;
            try { _track?.SetPreferredDevice(AndroidAudioDevices.ResolveDevice(value)); } catch { }
        }
    }

    public event EventHandler? PlaybackStopped;

    public void Init(ISampleProvider source)
    {
        lock (_sync)
        {
            _source = source;
            _buffer = new float[Math.Min(ChunkSamples, Math.Max(1024, source.WaveFormat.SampleRate / 8) * source.WaveFormat.Channels)];
        }
    }

    public void Init(IWaveProvider source) => Init(source.ToSampleProvider());

    public void Play()
    {
        lock (_sync)
        {
            if (_source == null) return;

            if (_track == null)
            {
                if (!TryCreateTrack(_source.WaveFormat)) return;
                _running = true;
                _paused = false;
                _renderThread = new Thread(RenderLoop)
                {
                    IsBackground = true,
                    Name = "SongRequest.AudioTrack",
                    Priority = ThreadPriority.AboveNormal,
                };
                _renderThread.Start();
            }

            try
            {
                _track?.Play();
                _paused = false;
                _state = NAudio.Wave.PlaybackState.Playing;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"AudioTrack play failed: {ex.Message}");
            }
        }

        RequestAudioFocus();

        // Android 13+: the playback notification needs POST_NOTIFICATIONS. Ask the
        // first time the user actually starts something — not at launch, where the
        // dialog would block the first interaction for no reason.
        AndroidPlatform.RequestNotificationPermissionOnce();
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_track == null) return;
            try { _track.Pause(); } catch { }
            _paused = true;
            _state = NAudio.Wave.PlaybackState.Paused;
        }
    }

    public void Stop()
    {
        Thread? thread;
        AudioTrack? track;

        lock (_sync)
        {
            _running = false;
            thread = _renderThread;
            track = _track;
            _renderThread = null;
            _track = null;
            _state = NAudio.Wave.PlaybackState.Stopped;
            _paused = false;
        }

        try { track?.Pause(); } catch { }
        try { track?.Flush(); } catch { }
        try { track?.Release(); } catch { }
        try { thread?.Join(250); } catch { }

        AbandonAudioFocus();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    // ── Track / render loop ─────────────────────────────────────────────────

    private bool TryCreateTrack(WaveFormat format)
    {
        try
        {
            var rate = format.SampleRate;
            var channels = format.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;

            // Float PCM keeps the engine's samples untouched; some devices only
            // support 16-bit, in which case the render loop converts.
            _useFloat = SupportsFloat(rate, channels);
            var encoding = _useFloat ? Encoding.PcmFloat : Encoding.Pcm16bit;

            var minBuffer = AudioTrack.GetMinBufferSize(rate, channels, encoding);
            if (minBuffer <= 0) minBuffer = rate * (channels == ChannelOut.Mono ? 1 : 2) * 2;

            var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!;

            var audioFormat = new AudioFormat.Builder()
                .SetEncoding(encoding)!
                .SetSampleRate(rate)!
                .SetChannelMask(channels)!
                .Build()!;

            _track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)!
                .SetAudioFormat(audioFormat)!
                .SetBufferSizeInBytes(minBuffer * BufferMultiplier)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;

            _shortBuffer = _useFloat ? null : new short[_buffer.Length];
            Volume = _volume;
            Android.Util.Log.Debug("SongRequest", $"AudioTrack ready: {rate}Hz {channels} {(_useFloat ? "float" : "pcm16")} buffer={minBuffer * BufferMultiplier} bytes");

            if (_deviceNumber >= 0)
                _track.SetPreferredDevice(AndroidAudioDevices.ResolveDevice(_deviceNumber));

            return _track.State == AudioTrackState.Initialized;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"AudioTrack init failed: {ex}");
            try { _track?.Release(); } catch { }
            _track = null;
            return false;
        }
    }

    private static bool SupportsFloat(int rate, ChannelOut channels)
    {
        try { return AudioTrack.GetMinBufferSize(rate, channels, Encoding.PcmFloat) > 0; }
        catch { return false; }
    }

    private void RenderLoop()
    {
        while (true)
        {
            AudioTrack? track;
            ISampleProvider? source;
            lock (_sync)
            {
                if (!_running) return;
                track = _track;
                source = _source;
            }
            if (track == null || source == null) return;

            if (_paused)
            {
                Thread.Sleep(15);
                continue;
            }

            int read;
            try { read = source.Read(_buffer, 0, _buffer.Length); }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"Audio read failed: {ex.Message}");
                read = 0;
            }

            if (read <= 0)
            {
                // End of stream: the mixer used for music pads with silence and
                // never reports 0, so this is a real end (chime, clip, preview).
                Android.Util.Log.Debug("SongRequest", $"AudioTrack source returned 0 after {_samplesWritten} samples -> stopping");
                Stop();
                return;
            }

            _samplesWritten += read;

            try
            {
                if (_useFloat)
                {
                    track.Write(_buffer, 0, read, WriteMode.Blocking);
                }
                else
                {
                    var shorts = _shortBuffer ??= new short[_buffer.Length];
                    for (int i = 0; i < read; i++)
                    {
                        var v = _buffer[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        shorts[i] = (short)Math.Round(v * 32767f);
                    }
                    track.Write(shorts, 0, read, WriteMode.Blocking);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"AudioTrack write failed after {_samplesWritten} samples: {ex}");
                return;
            }
        }
    }

    // ── Audio focus ─────────────────────────────────────────────────────────

    private void RequestAudioFocus()
    {
        var manager = _audioManager;
        if (manager == null) return;

        try
        {
            if (_focusRequest == null)
            {
                var attributes = new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!;

                _focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                    .SetAudioAttributes(attributes)!
                    .SetWillPauseWhenDucked(true)!
                    .SetOnAudioFocusChangeListener(new FocusListener(this))!
                    .Build()!;
            }

            manager.RequestAudioFocus(_focusRequest);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Audio focus request failed: {ex.Message}");
        }
    }

    private void AbandonAudioFocus()
    {
        var manager = _audioManager;
        var request = _focusRequest;
        if (manager == null || request == null) return;
        try { manager.AbandonAudioFocusRequest(request); } catch { }
    }

    private void OnFocusChanged(AudioFocus focusChange)
    {
        switch (focusChange)
        {
            case AudioFocus.Loss:
            case AudioFocus.LossTransient:
                // Another app took over: pause and remember to resume for a
                // transient loss (a notification sound must not kill the set).
                _resumeOnFocusGain = focusChange == AudioFocus.LossTransient && PlaybackState == NAudio.Wave.PlaybackState.Playing;
                if (PlaybackState == NAudio.Wave.PlaybackState.Playing) Pause();
                break;

            case AudioFocus.LossTransientCanDuck:
                // Ducked by the OS (e.g. navigation prompt): lower our own volume
                // instead of stopping, so the music stays audible underneath.
                try { _track?.SetVolume(_volume * 0.2f); } catch { }
                break;

            case AudioFocus.Gain:
                try { _track?.SetVolume(_volume); } catch { }
                if (_resumeOnFocusGain)
                {
                    _resumeOnFocusGain = false;
                    Play();
                }
                break;
        }
    }

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly AndroidAudioOutput _owner;

        public FocusListener(AndroidAudioOutput owner) => _owner = owner;

        public void OnAudioFocusChange(AudioFocus focusChange) => _owner.OnFocusChanged(focusChange);
    }
}
