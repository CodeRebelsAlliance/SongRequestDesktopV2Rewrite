using NAudio.Wave;
using System.Net;
using SongRequestDesktopV2Rewrite;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>In-memory <see cref="IAudioOutput"/> that records calls and can emit PlaybackStopped.</summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    public int DeviceNumber { get; set; } = -1;
    public float Volume { get; set; } = 1f;
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    public event EventHandler? PlaybackStopped;

    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public ISampleProvider? Source { get; private set; }
    public IWaveProvider? WaveSource { get; private set; }

    /// <summary>When true, <see cref="Play"/> immediately reports PlaybackStopped (end of stream).</summary>
    public bool StopOnPlay { get; set; }

    public void Init(ISampleProvider source) => Source = source;

    public void Init(IWaveProvider source) => WaveSource = source;

    public void Play()
    {
        PlayCount++;
        PlaybackState = PlaybackState.Playing;
        if (StopOnPlay) RaiseStopped();
    }

    public void Pause()
    {
        PauseCount++;
        PlaybackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        StopCount++;
        PlaybackState = PlaybackState.Stopped;
    }

    public void RaiseStopped()
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => PlaybackState = PlaybackState.Stopped;
}

/// <summary>Deterministic in-memory <see cref="IAudioSource"/>.</summary>
internal sealed class FakeAudioSource : IAudioSource, ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public FakeAudioSource(int sampleRate = 44100, int channels = 2, int frames = 100, float value = 0.25f)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _samples = new float[frames * channels];
        for (int i = 0; i < _samples.Length; i++) _samples[i] = value;
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_samples.Length / (WaveFormat.SampleRate * WaveFormat.Channels));

    public TimeSpan CurrentTime { get; set; }

    public long Length => _samples.Length * 4L;

    public long Position
    {
        get => _position * 4L;
        set => _position = (int)Math.Clamp(value / 4L, 0, _samples.Length);
    }

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

/// <summary>In-memory <see cref="IMidiService"/> that records sends and lets tests raise input.</summary>
internal sealed class FakeMidiService : IMidiService
{
    public event EventHandler<MidiMessage>? MidiMessageReceived;
    public event EventHandler<string>? ErrorOccurred;

    public bool IsEnabled { get; set; }
    public int? InputDeviceNumber { get; private set; }
    public int? OutputDeviceNumber { get; private set; }

    public List<MidiDeviceInfo> Inputs { get; } = new();
    public List<MidiDeviceInfo> Outputs { get; } = new();
    public List<MidiMessage> Sent { get; } = new();

    public List<MidiDeviceInfo> GetInputDevices() => Inputs;
    public List<MidiDeviceInfo> GetOutputDevices() => Outputs;

    public bool ConnectInput(int deviceNumber) { InputDeviceNumber = deviceNumber; return true; }
    public bool ConnectOutput(int deviceNumber) { OutputDeviceNumber = deviceNumber; return true; }
    public void DisconnectDevices() { InputDeviceNumber = null; OutputDeviceNumber = null; }

    public void SendNoteOn(int channel, int note, int velocity)
        => Sent.Add(new MidiMessage(MidiMessageType.NoteOn, channel, note, velocity, "NoteOn"));

    public void SendNoteOff(int channel, int note)
        => Sent.Add(new MidiMessage(MidiMessageType.NoteOff, channel, note, 0, "NoteOff"));

    public void SendControlChange(int channel, int controller, int value)
        => Sent.Add(new MidiMessage(MidiMessageType.ControlChange, channel, controller, value, "ControlChange"));

    public void Raise(MidiMessage message) => MidiMessageReceived?.Invoke(this, message);

    public void RaiseError(string message) => ErrorOccurred?.Invoke(this, message);

    public void Dispose() { }
}

/// <summary>In-memory <see cref="IGlobalHotkeyBackend"/> for testing the hotkey service.</summary>
internal sealed class FakeGlobalHotkeyBackend : IGlobalHotkeyBackend
{
    public bool IsSupported { get; set; } = true;

    public List<(int Id, HotkeyGesture Gesture)> Registered { get; } = new();
    public int UnregisterAllCount { get; private set; }

    public event EventHandler<int>? HotkeyPressed;

    public bool Register(int id, HotkeyGesture gesture)
    {
        Registered.Add((id, gesture));
        return true;
    }

    public void UnregisterAll()
    {
        UnregisterAllCount++;
        Registered.Clear();
    }

    public void Raise(int id) => HotkeyPressed?.Invoke(this, id);

    public void Dispose() { }
}

/// <summary>HTTP handler that returns a canned response, for testing services that take an HttpClient.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public string? LastUrl { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastBody { get; private set; }
    public IReadOnlyDictionary<string, string> LastHeaders { get; private set; } = new Dictionary<string, string>();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.ToString();
        LastMethod = request.Method.Method;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
        LastHeaders = headers;

        if (request.Content != null)
            LastBody = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        });
    }
}

/// <summary>In-memory <see cref="ISystemMediaSession"/> for testing <see cref="MediaSessionBridge"/>.</summary>
internal sealed class FakeSystemMediaSession : ISystemMediaSession
{
    public bool IsSupported { get; set; } = true;
    public string BackendName { get; set; } = "fake";

    public MediaMetadata? Metadata { get; private set; }
    public MediaPlaybackState State { get; private set; } = MediaPlaybackState.Stopped;
    public TimeSpan Position { get; private set; }
    public int MetadataUpdates { get; private set; }
    public int PlaybackUpdates { get; private set; }
    public bool Disposed { get; private set; }

    public void UpdateMetadata(MediaMetadata? metadata)
    {
        Metadata = metadata;
        MetadataUpdates++;
    }

    public void UpdatePlayback(MediaPlaybackState state, TimeSpan position)
    {
        State = state;
        Position = position;
        PlaybackUpdates++;
    }

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? TogglePlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? PositionSetRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler? RaiseRequested;
    public event EventHandler? QuitRequested;

    public void RaisePlay() => PlayRequested?.Invoke(this, EventArgs.Empty);
    public void RaisePause() => PauseRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseTogglePlayPause() => TogglePlayPauseRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseNext() => NextRequested?.Invoke(this, EventArgs.Empty);
    public void RaisePrevious() => PreviousRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseStop() => StopRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseSeek(TimeSpan offset) => SeekRequested?.Invoke(this, offset);
    public void RaisePositionSet(TimeSpan position) => PositionSetRequested?.Invoke(this, position);
    public void RaiseVolume(double volume) => VolumeRequested?.Invoke(this, volume);
    public void RaiseRaise() => RaiseRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseQuit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Disposed = true;
}
