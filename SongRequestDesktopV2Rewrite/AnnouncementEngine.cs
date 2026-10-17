using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// WPF-free announcement engine.
///
/// The announcement feature ducks the music output (optionally playing a short
/// pre-announcement chime) while the operator speaks. This replaces the logic
/// that used to live in the WPF <c>AnnouncementWindow</c> so the feature can be
/// driven from the web UI and from remote control (MIDI/keyboard) without any
/// WPF dependency.
/// </summary>
public sealed class AnnouncementEngine : IDisposable
{
    private readonly MusicEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _isActive;
    private bool _isTransitioning;
    private bool _disposed;
    private IAudioOutput? _chimeOutput;
    private IAudioSource? _chimeReader;

    /// <summary>Provides the current configuration (overridable in tests).</summary>
    public Func<Config?> ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;

    /// <summary>Path to the pre-announcement chime (overridable in tests).</summary>
    public string ChimePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "announcement.mp3");

    /// <summary>Creates the chime source (overridable in tests).</summary>
    public Func<string, IAudioSource> SourceFactory { get; set; } = AudioSourceFactory.Open;

    /// <summary>Creates the chime output (overridable in tests).</summary>
    public Func<IAudioOutput> OutputFactory { get; set; } = AudioOutputFactory.Create;

    public event Action<object>? StateChanged;

    public bool IsActive => _isActive;

    public double DimDb => ConfigAccessor()?.AnnouncementDimDb ?? 20.0;
    public bool PlaySoundEnabled => ConfigAccessor()?.AnnouncementPlaySound ?? true;
    public bool PushToTalkEnabled => ConfigAccessor()?.AnnouncementPushToTalk ?? true;

    public AnnouncementEngine(MusicEngine engine)
    {
        _engine = engine;
    }

    private string LiveText => PushToTalkEnabled ? "Live: hold to talk" : "Live: click to end";

    public object GetState() => BuildState(_isActive ? "live" : "idle",
        _isActive ? LiveText : (_isTransitioning ? "Preparing..." : "Ready"));

    private object BuildState(string state, string text) => new
    {
        isActive = _isActive,
        state,
        text,
        dimDb = DimDb,
        playSound = PlaySoundEnabled,
        pushToTalk = PushToTalkEnabled
    };

    private void PushState(string state, string text) => StateChanged?.Invoke(BuildState(state, text));

    /// <summary>Re-publishes the current state (e.g. after remote settings changes).</summary>
    public void NotifySettingsChanged() => PushState(_isActive ? "live" : "idle", _isActive ? LiveText : "Ready");

    public async Task StartAsync()
    {
        if (_disposed || _isActive || _isTransitioning) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isActive || _isTransitioning) return;
            _isTransitioning = true;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                PushState("preparing", "Preparing announcement...");
                await _engine.FadeAnnouncementDuckingAsync(DimDb, 500).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                if (PlaySoundEnabled)
                    await PlayChimeAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                _isActive = true;
                PushState("live", LiveText);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _isTransitioning = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        if (!_isActive && !_isTransitioning) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cts?.Cancel();
            _isActive = false;
            _isTransitioning = true;
            try
            {
                PushState("preparing", "Restoring music...");
                await _engine.FadeAnnouncementRestoreAsync(1000).ConfigureAwait(false);
            }
            finally
            {
                _isTransitioning = false;
                PushState("idle", "Ready");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PressOrToggleAsync()
    {
        if (PushToTalkEnabled) await StartAsync().ConfigureAwait(false);
        else if (_isActive) await StopAsync().ConfigureAwait(false);
        else await StartAsync().ConfigureAwait(false);
    }

    public async Task ReleaseAsync()
    {
        if (PushToTalkEnabled) await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Restores the music if an announcement is in progress (called on window close).</summary>
    public async Task OnClosedAsync()
    {
        if (_isActive || _isTransitioning)
            await StopAsync().ConfigureAwait(false);
    }

    private async Task PlayChimeAsync(CancellationToken token)
    {
        var path = ChimePath;
        if (!File.Exists(path))
        {
            try { await Task.Delay(600, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var reader = SourceFactory(ChimePath);
            var output = OutputFactory();
            output.Init(reader.ToSampleProvider());
            output.PlaybackStopped += (_, _) => tcs.TrySetResult(true);
            _chimeReader = reader;
            _chimeOutput = output;

            using var registration = token.Register(() => tcs.TrySetCanceled(token));
            output.Play();
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { _chimeOutput?.Stop(); } catch { }
            try { _chimeOutput?.Dispose(); } catch { }
            try { _chimeReader?.Dispose(); } catch { }
            _chimeOutput = null;
            _chimeReader = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _chimeOutput?.Stop(); } catch { }
        try { _chimeOutput?.Dispose(); } catch { }
        try { _chimeReader?.Dispose(); } catch { }
        _chimeOutput = null;
        _chimeReader = null;
    }
}
