using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>A serializable snapshot of the sleep timer for the UI and API.</summary>
public sealed class SleepTimerState
{
    public bool IsActive { get; init; }
    public bool IsFading { get; init; }
    public bool WaitingForTrackEnd { get; init; }
    public DateTime? EndsAtUtc { get; init; }
    public double DurationSeconds { get; init; }
    public double RemainingSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public bool PlayLastSongToEnd { get; init; }
    public string Phase { get; init; } = "idle";
}

/// <summary>
/// Counts down to a stop time, fades the music out and stops playback.
///
/// "Play the last song to the end" keeps the current track playing instead of
/// cutting it off: the queue is held at the current song, and the fade runs over
/// the final <see cref="SleepTimerState.FadeOutSeconds"/> of that song.
///
/// The service is deliberately independent of wall-clock timer granularity:
/// <see cref="TickAt"/> is public so tests can drive it without sleeping.
/// </summary>
public sealed class SleepTimerService : IDisposable
{
    private readonly MusicEngine _engine;
    private readonly object _sync = new();
    private Timer? _timer;
    private CancellationTokenSource? _runCts;
    private DateTime? _endsAtUtc;
    private DateTime _startedAtUtc;
    private double _durationSeconds;
    private double _fadeOutSeconds = 10;
    private bool _playLastSongToEnd = true;
    private bool _fading;
    private bool _waitingForTrackEnd;
    private bool _disposed;
    private double _lastPublishedRemaining = -1;

    public SleepTimerService(MusicEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Clock, overridable in tests.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Delay used by fades, overridable so tests run instantly.</summary>
    public Func<int, Task> Delay { get; set; } = ms => Task.Delay(ms);

    /// <summary>When false no background timer is created; call <see cref="TickAt"/> manually.</summary>
    public bool AutoTick { get; set; } = true;

    /// <summary>Raised whenever the remaining time, phase or options change.</summary>
    public event Action<SleepTimerState>? StateChanged;

    /// <summary>Raised once when the timer reached its end and playback was stopped.</summary>
    public event Action? Expired;

    public SleepTimerState GetState()
    {
        lock (_sync)
        {
            var now = UtcNow();
            double remaining = _endsAtUtc.HasValue
                ? Math.Max(0, (_endsAtUtc.Value - now).TotalSeconds)
                : 0;

            return new SleepTimerState
            {
                IsActive = _endsAtUtc.HasValue,
                IsFading = _fading,
                WaitingForTrackEnd = _waitingForTrackEnd,
                EndsAtUtc = _endsAtUtc,
                DurationSeconds = _durationSeconds,
                RemainingSeconds = remaining,
                FadeOutSeconds = _fadeOutSeconds,
                PlayLastSongToEnd = _playLastSongToEnd,
                Phase = _endsAtUtc == null
                    ? "idle"
                    : _fading
                        ? "fading"
                        : _waitingForTrackEnd
                            ? "finishing-track"
                            : "counting"
            };
        }
    }

    /// <summary>Starts (or restarts) a timer. Duration is in minutes.</summary>
    public SleepTimerState Start(double minutes, double? fadeOutSeconds = null, bool? playLastSongToEnd = null)
        => Start(TimeSpan.FromMinutes(Math.Max(0.05, minutes)), fadeOutSeconds, playLastSongToEnd);

    public SleepTimerState Start(TimeSpan duration, double? fadeOutSeconds = null, bool? playLastSongToEnd = null)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

        lock (_sync)
        {
            CancelRunLocked(restoreVolume: true);

            var now = UtcNow();
            _startedAtUtc = now;
            _durationSeconds = duration.TotalSeconds;
            _endsAtUtc = now + duration;
            if (fadeOutSeconds.HasValue)
                _fadeOutSeconds = Math.Clamp(fadeOutSeconds.Value, 0, 60);
            if (playLastSongToEnd.HasValue)
                _playLastSongToEnd = playLastSongToEnd.Value;

            _fading = false;
            _waitingForTrackEnd = false;
            _engine.SleepTimerHoldsPlayback = false;
            _lastPublishedRemaining = _durationSeconds;

            if (AutoTick) EnsureTimerLocked();
        }

        var state = Publish();
        return state;
    }

    public SleepTimerState Extend(TimeSpan extra)
    {
        lock (_sync)
        {
            if (_endsAtUtc == null) return GetState();
            var next = _endsAtUtc.Value + extra;
            var minimum = UtcNow();
            if (next <= minimum) next = minimum;
            _endsAtUtc = next;
            _durationSeconds = Math.Max(0, (_endsAtUtc.Value - _startedAtUtc).TotalSeconds);
        }
        return Publish();
    }

    public SleepTimerState SetOptions(double? fadeOutSeconds = null, bool? playLastSongToEnd = null)
    {
        lock (_sync)
        {
            if (fadeOutSeconds.HasValue) _fadeOutSeconds = Math.Clamp(fadeOutSeconds.Value, 0, 60);
            if (playLastSongToEnd.HasValue) _playLastSongToEnd = playLastSongToEnd.Value;
        }
        return Publish();
    }

    public SleepTimerState Cancel()
    {
        lock (_sync)
        {
            CancelRunLocked(restoreVolume: true);
            _endsAtUtc = null;
            _durationSeconds = 0;
            _fading = false;
            _waitingForTrackEnd = false;
            _engine.SleepTimerHoldsPlayback = false;
            _lastPublishedRemaining = -1;
        }
        return Publish();
    }

    /// <summary>Drives the timer. The background timer calls this once per second.</summary>
    public void Tick() => TickAt(UtcNow());

    /// <summary>Test/API-friendly tick with explicit time.</summary>
    public void TickAt(DateTime now)
    {
        DateTime? endsAt;
        bool shouldPublish = false;

        lock (_sync)
        {
            if (_disposed || _endsAtUtc == null) return;
            endsAt = _endsAtUtc;
            double remaining = (endsAt.Value - now).TotalSeconds;

            if (remaining > 0)
            {
                // Publish at most once per second, and only when the displayed
                // second actually changes (the UI pill shows mm:ss).
                var rounded = Math.Ceiling(remaining);
                if (Math.Abs(rounded - _lastPublishedRemaining) >= 1)
                {
                    _lastPublishedRemaining = rounded;
                    shouldPublish = true;
                }
            }
            else if (!_fading && !_waitingForTrackEnd)
            {
                shouldPublish = true;
            }
        }

        if (shouldPublish) Publish();

        if (endsAt.HasValue && (endsAt.Value - now).TotalSeconds <= 0)
            _ = HandleExpiryAsync();
    }

    private async Task HandleExpiryAsync()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_endsAtUtc == null || _fading || _waitingForTrackEnd) return;
            _runCts = new CancellationTokenSource();
            token = _runCts.Token;
        }

        try
        {
            // Let the final song finish first, if requested. The engine's hold flag
            // prevents the queue/crossfade from advancing into the next track.
            if (_playLastSongToEnd && _engine.IsPlaying)
            {
                lock (_sync) _waitingForTrackEnd = true;
                _engine.SleepTimerHoldsPlayback = true;
                Publish();

                while (!token.IsCancellationRequested && _engine.IsPlaying)
                {
                    double remaining = _engine.CurrentTrackRemaining?.TotalSeconds ?? 0;
                    if (remaining <= Math.Max(0.35, _fadeOutSeconds)) break;
                    await Delay(500).ConfigureAwait(false);
                }
            }

            if (token.IsCancellationRequested) return;

            lock (_sync)
            {
                _waitingForTrackEnd = false;
                _fading = true;
            }
            Publish();

            double remainingSeconds = _engine.CurrentTrackRemaining?.TotalSeconds ?? _fadeOutSeconds;
            double fadeSeconds = _engine.IsPlaying
                ? Math.Min(_fadeOutSeconds, Math.Max(0.5, remainingSeconds))
                : _fadeOutSeconds;
            await FadeOutAsync(fadeSeconds, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            _engine.Stop();
            Complete();
        }
        catch (OperationCanceledException)
        {
            // Cancelled / restarted by the user: the caller restores the volume.
        }
        catch
        {
            // A failed fade must still stop playback rather than leave the timer stuck.
            try { _engine.Stop(); } catch { }
            Complete();
        }
    }

    private async Task FadeOutAsync(double seconds, CancellationToken token)
    {
        if (seconds <= 0)
        {
            _engine.SetSleepFadeFactor(0);
            return;
        }

        int steps = Math.Max(1, (int)Math.Round(seconds * 10));
        int delay = Math.Max(10, (int)(seconds * 1000 / steps));
        for (int i = 1; i <= steps; i++)
        {
            if (token.IsCancellationRequested) return;
            float factor = 1f - (float)i / steps;
            _engine.SetSleepFadeFactor(factor);
            if (i < steps) await Delay(delay).ConfigureAwait(false);
        }
    }

    private void Complete()
    {
        lock (_sync)
        {
            _endsAtUtc = null;
            _durationSeconds = 0;
            _fading = false;
            _waitingForTrackEnd = false;
            _engine.SleepTimerHoldsPlayback = false;
            _lastPublishedRemaining = -1;
            _runCts?.Dispose();
            _runCts = null;
            StopTimerLocked();
        }

        _engine.SetSleepFadeFactor(1f);
        Publish();
        try { Expired?.Invoke(); } catch { }
    }

    private void CancelRunLocked(bool restoreVolume)
    {
        try { _runCts?.Cancel(); } catch { }
        try { _runCts?.Dispose(); } catch { }
        _runCts = null;
        StopTimerLocked();
        _engine.SleepTimerHoldsPlayback = false;
        if (restoreVolume) _engine.SetSleepFadeFactor(1f);
    }

    private void EnsureTimerLocked()
    {
        _timer ??= new Timer(_ =>
        {
            try { Tick(); } catch { }
        }, null, 250, 500);
    }

    private void StopTimerLocked()
    {
        var timer = _timer;
        _timer = null;
        if (timer == null) return;
        try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { timer.Dispose(); } catch { }
    }

    private SleepTimerState Publish()
    {
        var state = GetState();
        try { StateChanged?.Invoke(state); } catch { }
        return state;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _endsAtUtc = null;
            CancelRunLocked(restoreVolume: true);
        }
    }
}
