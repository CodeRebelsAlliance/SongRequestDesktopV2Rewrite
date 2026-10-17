using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Plays the audio chunks received during a Music Share session via the
/// cross-platform audio output (a BufferedWaveProvider fed to OpenAL).
/// </summary>
public sealed class MusicShareAudioPlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 2;

    private readonly object _lock = new();
    private IAudioOutput? _output;
    private BufferedWaveProvider? _buffer;
    private bool _playbackStarted;

    /// <summary>Creates the output device (overridable in tests).</summary>
    public Func<IAudioOutput> OutputFactory { get; set; } = AudioOutputFactory.Create;

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_lock)
            {
                if (_output != null)
                {
                    try { _output.Volume = _volume; } catch { }
                }
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_output != null) return;
            try
            {
                var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
                _buffer = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true
                };
                _output = OutputFactory();
                _output.Init(_buffer);
                _output.Volume = _volume;
                _output.PlaybackStopped += (_, _) => _playbackStarted = false;
                _playbackStarted = false;
            }
            catch
            {
                _output?.Dispose();
                _output = null;
                _buffer = null;
            }
        }
    }

    /// <summary>Feeds a base64-encoded float32 chunk. Starts playback once the buffer has warmed up.</summary>
    public void Feed(string? base64Data, int bufferLevel)
    {
        if (string.IsNullOrEmpty(base64Data)) return;
        lock (_lock)
        {
            if (_buffer == null || _output == null) return;
            try
            {
                var bytes = Convert.FromBase64String(base64Data);
                _buffer.AddSamples(bytes, 0, bytes.Length);

                if (!_playbackStarted && bufferLevel >= 3 && _buffer.BufferedBytes > 0)
                {
                    _output.Play();
                    _playbackStarted = true;
                }
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _buffer = null;
            _playbackStarted = false;
        }
    }

    public void Dispose() => Stop();
}
