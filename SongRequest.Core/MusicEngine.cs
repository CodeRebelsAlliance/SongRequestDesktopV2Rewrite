using System.Collections.ObjectModel;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// WPF-free player engine state.
///
/// Owns the play queue and the NAudio playback objects, extracted from the WPF
/// <c>MusicPlayer</c> window so the player's core can eventually run without WPF.
///
/// Playback uses the cross-platform OpenAL Soft output and the FFmpeg decoder;
/// NAudio.Core supplies only the portable sample graph.
/// </summary>
public sealed class MusicEngine
{
    public ObservableCollection<Song> Queue { get; } = new();

    // Playlist queue: songs from a playlist, consumed only after the main queue is empty.
    public ObservableCollection<Song> PlaylistQueue { get; } = new();

    // For overlapping crossfade we use MixingSampleProvider.
    public IAudioOutput Output { get; set; } = null!;
    public MixingSampleProvider Mixer { get; set; } = null!;
    public IAudioSource CurrentReader { get; set; } = null!;
    public IAudioSource NextReader { get; set; } = null!;

    // VolumeSampleProvider wrappers so volume ramps don't touch the underlying reader.Volume.
    public VolumeSampleProvider CurrentVolProvider { get; set; } = null!;
    public VolumeSampleProvider NextVolProvider { get; set; } = null!;

    public float Volume { get; set; } = 0.9f;
    public float AnnouncementOutputFactor { get; set; } = 1f;

    /// <summary>
    /// Sleep-timer fade factor. Kept separate from the announcement ducking factor
    /// so a running sleep fade cannot be undone by announcement restore logic.
    /// </summary>
    public float SleepFadeFactor { get; private set; } = 1f;

    /// <summary>
    /// While a sleep timer is letting the last song finish, the engine must not
    /// crossfade into (or advance to) the next queue item.
    /// </summary>
    public bool SleepTimerHoldsPlayback { get; set; }

    /// <summary>The countdown/fade service owned by this engine.</summary>
    public SleepTimerService SleepTimer { get; }

    /// <summary>The live equalizer state/service used by this engine.</summary>
    public EqualizerService Equalizer { get; set; } = EqualizerService.Instance;

    /// <summary>Equalizer stage in the live pipeline (null when stopped).</summary>
    public EqualizerProcessor? EqualizerProcessor { get; private set; }

    public bool RepeatEnabled { get; set; }

    /// <summary>Common mixer format (44.1 kHz stereo float).</summary>
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    /// <summary>Target loudness in LUFS used by volume normalization.</summary>
    public double TargetLoudness { get; set; } = -14.0;

    /// <summary>
    /// Provides the current configuration. Defaults to the app-wide singleton;
    /// overridable so tests can supply their own <see cref="Config"/>.
    /// </summary>
    public Func<Config?> ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;

    /// <summary>Delay used by announcement fades and crossfades. Overridable so tests run instantly.</summary>
    public Func<int, System.Threading.Tasks.Task> Delay { get; set; } = ms => System.Threading.Tasks.Task.Delay(ms);

    /// <summary>Mutates and persists the configuration (overridable in tests).</summary>
    public Action<Action<Config>> ConfigUpdater { get; set; } = updater => ConfigService.Instance.Update(updater);

    /// <summary>Whether audio is currently playing / active, and whether the user may control volume.</summary>
    public bool IsPlaying => Output?.PlaybackState == PlaybackState.Playing;
    public bool IsPlaybackActive => Output != null && Output.PlaybackState != PlaybackState.Stopped;
    public bool CanControlVolume => ConfigAccessor()?.NormalizeVolume != true;
    public double VolumeValue => System.Math.Clamp((double)Volume, 0.0, 1.0);

    // ──────────────────────────────────────────────────────────────
    //  Sample provider / loudness helpers (WPF-free)
    // ──────────────────────────────────────────────────────────────

    private readonly SemaphoreSlim _announcementVolumeGate = new SemaphoreSlim(1, 1);

    // While a bulk operation is running, per-item notifications are coalesced into one
    // update at the end (see BeginBulkUpdate). Adding a 500-song playlist used to fire
    // 500 full queue updates — each one serializing and resolving artwork for the whole
    // growing queue — which is what delayed playback by seconds on mobile.
    private int _bulkUpdateDepth;
    private bool _queueChangedDuringBatch;
    private bool _playlistQueueChangedDuringBatch;

    public MusicEngine()
    {
        SleepTimer = new SleepTimerService(this);

        // Wait times depend on every song before them, so they are recomputed *before*
        // the UI is told about the change: this notification is what pushes the queue
        // to the music player window, and it used to fire before the newly added song
        // got its wait time (the item then showed a stale value until the next update).
        Queue.CollectionChanged += (_, _) =>
        {
            if (_bulkUpdateDepth > 0)
            {
                _queueChangedDuringBatch = true;
                return;
            }
            ComputeQueueTimings();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        };
        PlaylistQueue.CollectionChanged += (_, _) =>
        {
            if (_bulkUpdateDepth > 0)
            {
                _playlistQueueChangedDuringBatch = true;
                return;
            }
            PlaylistQueueChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Coalesces queue notifications while many songs are added/removed. Dispose the
    /// result to send the single update (or one update per queue that actually changed).
    /// </summary>
    public IDisposable BeginBulkUpdate()
    {
        _bulkUpdateDepth++;
        return new BulkUpdateScope(this);
    }

    private sealed class BulkUpdateScope : IDisposable
    {
        private MusicEngine? _engine;

        public BulkUpdateScope(MusicEngine engine) => _engine = engine;

        public void Dispose()
        {
            var engine = _engine;
            if (engine == null) return;
            _engine = null;
            engine.EndBulkUpdate();
        }
    }

    private void EndBulkUpdate()
    {
        if (_bulkUpdateDepth == 0) return;
        if (--_bulkUpdateDepth > 0) return;

        if (_queueChangedDuringBatch && _playlistQueueChangedDuringBatch)
        {
            _queueChangedDuringBatch = false;
            _playlistQueueChangedDuringBatch = false;
            ComputeQueueTimings();
            QueuesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_queueChangedDuringBatch)
        {
            _queueChangedDuringBatch = false;
            ComputeQueueTimings();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_playlistQueueChangedDuringBatch)
        {
            _playlistQueueChangedDuringBatch = false;
            PlaylistQueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Queue mutation helpers (moved from the WPF window)
    // ──────────────────────────────────────────────────────────────

    public void ToggleRepeat() => RepeatEnabled = !RepeatEnabled;

    public void AddSong(Song s)
    {
        if (s == null) return;
        Queue.Add(s);   // CollectionChanged recomputes the wait times and notifies the UI
        var cfg = ConfigAccessor();
        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
            _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(s));
    }

    public void AddSongExternal(Song? s)
    {
        if (s == null) return;
        try
        {
            using var afr = AudioSourceFactory.Open(s.songPath);
            s.Duration = afr.TotalTime;
            s.length = TimeDisplay.Clock(s.Duration);
        }
        catch { }
        AddSong(s);
    }

    /// <summary>
    /// Appends a batch of songs with a single queue notification. Used by "play/queue
    /// playlist": calling <see cref="AddSong"/> per item made large playlists take
    /// seconds before playback could start.
    /// </summary>
    public void AddSongs(IEnumerable<Song> songs)
    {
        if (songs == null) return;

        var added = new List<Song>();
        using (BeginBulkUpdate())
        {
            foreach (var song in songs)
            {
                if (song == null) continue;
                Queue.Add(song);
                added.Add(song);
            }
        }

        var cfg = ConfigAccessor();
        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
        {
            // Warm the next few tracks only; StartPlayback calculates each track when
            // it actually starts, and calculating hundreds at once saturates the CPU.
            foreach (var song in added.Take(3))
                _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(song));
        }
    }

    public void AddNextSongExternal(Song? s)
    {
        if (s == null) return;
        try
        {
            using var afr = AudioSourceFactory.Open(s.songPath);
            s.Duration = afr.TotalTime;
            s.length = TimeDisplay.Clock(s.Duration);
        }
        catch { }

        if (Queue.Count > 1) Queue.Insert(1, s); else Queue.Add(s);
        // CollectionChanged recomputes the wait times and notifies the UI.

        var cfg = ConfigAccessor();
        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
            _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(s));
    }

    /// <summary>Creates a Song from a local file (WPF-free: no platform thumbnail).</summary>
    public bool TryCreateSongFromFile(string filePath, out Song? song, out string errorMessage)
    {
        song = null;
        errorMessage = string.Empty;
        try
        {
            using var afr = AudioSourceFactory.Open(filePath);
            var duration = afr.TotalTime;

            string title = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string artist = "Unknown";

            try
            {
                var tfile = global::TagLib.File.Create(filePath);
                if (!string.IsNullOrWhiteSpace(tfile.Tag.Title)) title = tfile.Tag.Title;
                if (tfile.Tag.Performers != null && tfile.Tag.Performers.Length > 0)
                {
                    artist = string.Join(", ", tfile.Tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                    if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown";
                }
            }
            catch { }

            var imageBase64 = TryGetEmbeddedImageBase64(filePath);
            song = new Song(title, artist, imageBase64, duration, filePath);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? TryGetEmbeddedImageBase64(string filePath)
    {
        try
        {
            var tfile = global::TagLib.File.Create(filePath);
            var pic = tfile.Tag.Pictures?.FirstOrDefault();
            if (pic?.Data?.Data != null && pic.Data.Data.Length > 0)
                return "data:" + (pic.MimeType ?? "image/jpeg") + ";base64," + Convert.ToBase64String(pic.Data.Data);
        }
        catch { }
        return null;
    }

    // ──────────────────────────────────────────────────────────────
    //  Announcement ducking + remote integration
    // ──────────────────────────────────────────────────────────────

    public bool RemoteAnnouncementIsActive => RemoteAnnouncementActiveProvider?.Invoke() ?? false;

    public double CrossfadeNormalized => CrossfadeMax <= 0 ? 0 : System.Math.Clamp(CrossfadeSeconds / CrossfadeMax, 0.0, 1.0);

    public void AdjustVolume(double delta)
    {
        if (!CanControlVolume) return;
        Volume = (float)System.Math.Clamp(Volume + delta, 0.0, 1.0);
        if (CurrentVolProvider != null) CurrentVolProvider.Volume = Volume;
        RequestVolumeSliderUpdate?.Invoke(Volume);
    }

    public void SetVolumeFromMidi(double normalizedValue)
    {
        if (!CanControlVolume) return;
        Volume = (float)System.Math.Clamp(normalizedValue, 0.0, 1.0);
        if (CurrentVolProvider != null) CurrentVolProvider.Volume = Volume;
        RequestVolumeSliderUpdate?.Invoke(Volume);
    }

    /// <summary>Sets the sleep-timer fade (1 = full volume, 0 = silent) and applies it.</summary>
    public void SetSleepFadeFactor(float factor)
    {
        SleepFadeFactor = System.Math.Clamp(factor, 0f, 1f);
        ApplyOutputVolume();
    }

    /// <summary>Re-applies every master-volume factor (announcement ducking × sleep fade).</summary>
    public void ApplyOutputVolume()
    {
        var output = Output;
        if (output == null) return;
        try { output.Volume = System.Math.Clamp(AnnouncementOutputFactor * SleepFadeFactor, 0f, 1f); } catch { }
    }

    /// <summary>Remaining time of the current track (null when nothing is playing).</summary>
    public TimeSpan? CurrentTrackRemaining
    {
        get
        {
            var reader = CurrentReader;
            if (reader == null) return null;
            try
            {
                var remaining = reader.TotalTime - reader.CurrentTime;
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }
            catch { return null; }
        }
    }

    public void AdjustCrossfade(double delta)
    {
        CrossfadeSeconds = System.Math.Clamp(CrossfadeSeconds + delta, 0.0, CrossfadeMax);
        RequestCrossfadeSliderUpdate?.Invoke(CrossfadeSeconds);
    }

    public void SetCrossfadeFromMidi(double normalizedValue)
    {
        CrossfadeSeconds = System.Math.Clamp(normalizedValue, 0.0, 1.0) * CrossfadeMax;
        RequestCrossfadeSliderUpdate?.Invoke(CrossfadeSeconds);
    }

    /// <summary>Sets the crossfade (in seconds) and syncs the host slider.</summary>
    public void SetCrossfade(double seconds)
    {
        CrossfadeSeconds = System.Math.Clamp(seconds, 0.0, CrossfadeMax);
        RequestCrossfadeSliderUpdate?.Invoke(CrossfadeSeconds);
    }

    public double RemoteAnnouncementDimNormalized
    {
        get
        {
            double dimDb = ConfigAccessor()?.AnnouncementDimDb ?? 20.0;
            return System.Math.Clamp(dimDb / 50.0, 0.0, 1.0);
        }
    }

    public bool RemoteAnnouncementPushToTalkEnabled => ConfigAccessor()?.AnnouncementPushToTalk ?? true;

    public async System.Threading.Tasks.Task FadeAnnouncementDuckingAsync(double dimDb, int durationMs)
    {
        var targetFactor = (float)System.Math.Clamp(System.Math.Pow(10.0, -System.Math.Abs(dimDb) / 20.0), 0.01, 1.0);
        await FadeAnnouncementOutputToAsync(targetFactor, durationMs).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task FadeAnnouncementRestoreAsync(int durationMs)
    {
        await FadeAnnouncementOutputToAsync(1f, durationMs).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task FadeAnnouncementOutputToAsync(float targetFactor, int durationMs)
    {
        await _announcementVolumeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            float startFactor = AnnouncementOutputFactor;
            AnnouncementOutputFactor = targetFactor;

            var output = Output;
            if (output == null) return;

            int steps = System.Math.Max(10, durationMs / 20);
            int delay = System.Math.Max(10, durationMs / steps);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float eased = 1f - (1f - t) * (1f - t);
                float current = startFactor + (targetFactor - startFactor) * eased;
                try { output.Volume = System.Math.Clamp(current * SleepFadeFactor, 0f, 1f); } catch { break; }
                if (i < steps) await Delay(delay).ConfigureAwait(false);
            }

            try { output.Volume = System.Math.Clamp(targetFactor * SleepFadeFactor, 0f, 1f); } catch { }
        }
        finally
        {
            _announcementVolumeGate.Release();
        }
    }

    public async System.Threading.Tasks.Task RemoteAnnouncementActionAsync()
    {
        if (RemoteAnnouncementHandler != null) await RemoteAnnouncementHandler().ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task RemoteAnnouncementReleaseAsync()
    {
        if (RemoteAnnouncementReleaseHandler != null) await RemoteAnnouncementReleaseHandler().ConfigureAwait(false);
    }

    public void RemoteToggleAnnouncementPlaySound()
    {
        bool enabled = !(ConfigAccessor()?.AnnouncementPlaySound ?? true);
        ConfigUpdater(cfg => cfg.AnnouncementPlaySound = enabled);
        AnnouncementSettingsChanged?.Invoke();
    }

    public void RemoteToggleAnnouncementPushToTalk()
    {
        bool enabled = !(ConfigAccessor()?.AnnouncementPushToTalk ?? true);
        ConfigUpdater(cfg => cfg.AnnouncementPushToTalk = enabled);
        AnnouncementSettingsChanged?.Invoke();
    }

    public void RemoteAdjustAnnouncementDimDb(double delta)
    {
        double current = ConfigAccessor()?.AnnouncementDimDb ?? 20.0;
        double target = System.Math.Clamp(current + delta, 0.0, 50.0);
        ConfigUpdater(cfg => cfg.AnnouncementDimDb = target);
        AnnouncementSettingsChanged?.Invoke();
    }

    public void RemoteSetAnnouncementDimDbFromMidi(double normalizedValue)
    {
        double target = System.Math.Clamp(normalizedValue, 0.0, 1.0) * 50.0;
        ConfigUpdater(cfg => cfg.AnnouncementDimDb = target);
        AnnouncementSettingsChanged?.Invoke();
    }

    public ISampleProvider GetMixerInput(IAudioSource reader)
    {
        ISampleProvider sp = reader.ToSampleProvider();

        // Ensure stereo
        if (sp.WaveFormat.Channels == 1 && Format.Channels == 2)
            sp = new MonoToStereoSampleProvider(sp);

        // Resample if needed
        if (sp.WaveFormat.SampleRate != Format.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, Format.SampleRate);

        return sp;
    }

    public ISampleProvider GetSampleProviderCompatible(IAudioSource reader) => GetMixerInput(reader);

    /// <summary>
    /// Selects the output device (-1 = system default).
    ///
    /// <see cref="OutputDeviceNumber"/> alone is only read when an output is created, so
    /// when music is playing the running output is re-created on the new device. The
    /// sample reader is untouched, so playback continues from the same position (with
    /// the usual short gap a device switch causes).
    /// </summary>
    public void SetOutputDevice(int deviceNumber)
    {
        if (OutputDeviceNumber == deviceNumber) return;
        OutputDeviceNumber = deviceNumber;

        if (!IsPlaybackActive || Output == null) return;

        try
        {
            var old = Output;
            var replacement = OutputFactory();
            replacement.DeviceNumber = deviceNumber;
            replacement.Volume = Math.Clamp(AnnouncementOutputFactor * SleepFadeFactor, 0f, 1f);
            replacement.Init(CapturingProvider ?? (ISampleProvider)Mixer);
            replacement.PlaybackStopped += Output_PlaybackStopped;
            replacement.Play();

            Output = replacement;

            // Detach first so the teardown of the old output cannot look like the end
            // of the track.
            try { old.PlaybackStopped -= Output_PlaybackStopped; } catch { }
            try { old.Stop(); } catch { }
            try { old.Dispose(); } catch { }
        }
        catch
        {
            // Keep playing on whatever device still works.
        }
    }

    public async Task CalculateLoudnessForQueueAsync()
    {
        foreach (var song in Queue)
        {
            if (song.PerceivedLoudness < 0)
                await Task.Run(() => CalculateSongLoudness(song)).ConfigureAwait(false);
        }
    }

    public void CalculateSongLoudness(Song song)
    {
        try
        {
            if (!System.IO.File.Exists(song.songPath)) return;

            using var reader = AudioSourceFactory.Open(song.songPath);
            float[] buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            double sumSquares = 0;
            long totalSamples = 0;
            int samplesRead;

            // Sample throughout the file (every 10 seconds) to avoid reading everything.
            int sampleInterval = reader.WaveFormat.SampleRate * reader.WaveFormat.Channels * 10;

            while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < samplesRead; i++)
                    sumSquares += buffer[i] * buffer[i];
                totalSamples += samplesRead;

                if (reader.Position + sampleInterval < reader.Length)
                    reader.Position += sampleInterval;
            }

            if (totalSamples > 0)
            {
                double rms = Math.Sqrt(sumSquares / totalSamples);
                double lufs = 20 * Math.Log10(rms + 0.0000001) - 0.691;
                song.PerceivedLoudness = lufs;
                System.Diagnostics.Debug.WriteLine($"Calculated loudness for {song.Title}: {lufs:F1} LUFS");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to calculate loudness for {song.Title}: {ex.Message}");
            song.PerceivedLoudness = TargetLoudness;
        }
    }

    public float CalculateNormalizedVolume(Song song, float baseVolume)
    {
        var cfg = ConfigAccessor();
        // -1 is the "not calculated yet" sentinel; treat it like a missing value
        // instead of applying a huge gain while the background scan is running.
        var loudnessUnknown = song.PerceivedLoudness < -50 ||
                              System.Math.Abs(song.PerceivedLoudness + 1.0) < 0.0001;
        if (cfg == null || !cfg.NormalizeVolume || !cfg.NormalizationActive ||
            cfg.DefaultVolume < 0 || loudnessUnknown)
        {
            return baseVolume;
        }

        double loudnessDiff = TargetLoudness - song.PerceivedLoudness;
        float gainAdjustment = (float)Math.Pow(10, loudnessDiff / 20.0);
        float normalizedVolume = cfg.DefaultVolume * gainAdjustment;
        return Math.Clamp(normalizedVolume, 0.0f, 1.0f);
    }

    // Audio capture for Music Share / visualization.
    public CapturingSampleProvider? CapturingProvider { get; set; }

    public int CaptureChannels => CapturingProvider?.WaveFormat.Channels ?? 2;
    public int CaptureSampleRate => CapturingProvider?.WaveFormat.SampleRate ?? 44100;

    // ──────────────────────────────────────────────────────────────
    //  Playback pipeline (WPF-free; UI updates via hooks)
    // ──────────────────────────────────────────────────────────────

    public int OutputDeviceNumber { get; set; } = -1;

    /// <summary>Creates the audio output; tests replace it to avoid touching real devices.</summary>
    public Func<IAudioOutput> OutputFactory { get; set; } = AudioOutputFactory.Create;

    public double CrossfadeSeconds { get; set; } = 4;
    public double CrossfadeMax { get; set; } = 10;
    public bool IsUserSeeking { get; set; }
    public bool IsUserAdjustingVolume { get; set; }
    public bool IsCrossfading { get; private set; }

    /// <summary>Marshals work onto the UI thread (set by the WPF host).</summary>
    public Action<Action>? InvokeOnUi;

    // UI hooks (set by the WPF host).
    public Action<Song?>? NowPlayingChanged;
    public Action<bool>? PlayStateChanged;
    public Action<float>? RequestVolumeSliderUpdate;
    public Action<double>? RequestCrossfadeSliderUpdate;
    public Action<double, TimeSpan, TimeSpan>? PositionChanged;
    public Action? QueueEmptied;

    // Events (subscribed by hosts/services so the engine is not tied to the WPF window).
    public event EventHandler<NowPlayingEventArgs>? NowPlayingTick;
    public event EventHandler<float[]>? AudioSamplesCaptured;
    public event EventHandler? QueueChanged;
    public event EventHandler? PlaylistQueueChanged;

    /// <summary>
    /// Raised once by a bulk update that changed both queues (instead of one event
    /// per collection). Subscribers that render both queues get a single update.
    /// </summary>
    public event EventHandler? QueuesChanged;

    // Announcement / share / visualization integration hooks (set by the host).
    public Func<System.Threading.Tasks.Task>? RemoteAnnouncementHandler { get; set; }
    public Func<System.Threading.Tasks.Task>? RemoteAnnouncementReleaseHandler { get; set; }
    public Func<bool>? RemoteAnnouncementActiveProvider { get; set; }
    public Action? AnnouncementSettingsChanged { get; set; }
    public Action? OpenMusicShareHandler { get; set; }
    public Action? OpenVisualizationHandler { get; set; }
    public Action? AnnouncementOpenHandler { get; set; }

    private DateTime _lastSecondaryTickUtc = DateTime.MinValue;

    /// <summary>How often the playback tick runs (drives the progress bar and track changes).</summary>
    public int TickIntervalMs { get; set; } = 100;

    /// <summary>
    /// Whether the engine drives its own playback tick while playing. On by default;
    /// tests can turn it off and call <see cref="ProcessTick"/> themselves.
    /// </summary>
    public bool AutoTick { get; set; } = true;

    private System.Threading.Timer? _tickTimer;
    private int _tickTimerRunning;

    /// <summary>
    /// Starts the periodic tick that reports the position and notices the end of a
    /// track. The audio pipeline cannot signal EOF itself (the crossfade mixer pads
    /// with silence), so without this nothing would advance to the next song.
    /// </summary>
    private void EnsureTickTimer()
    {
        if (!AutoTick) return;
        if (System.Threading.Interlocked.CompareExchange(ref _tickTimerRunning, 1, 0) != 0) return;

        _tickTimer = new System.Threading.Timer(_ =>
        {
            // A throwing timer callback would kill the process.
            try { ProcessTick(); } catch { }
        }, null, TickIntervalMs, TickIntervalMs);
    }

    private void StopTickTimer()
    {
        var timer = System.Threading.Interlocked.Exchange(ref _tickTimer, null);
        if (timer != null)
        {
            try { timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
            try { timer.Dispose(); } catch { }
        }

        System.Threading.Volatile.Write(ref _tickTimerRunning, 0);
    }

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0f, 1f);
        if (CurrentVolProvider != null) CurrentVolProvider.Volume = Volume;
    }

    public void Seek(double normalizedPosition)
    {
        if (CurrentReader == null) return;
        double clamped = Math.Clamp(normalizedPosition, 0.0, 1.0);
        CurrentReader.CurrentTime = TimeSpan.FromSeconds(clamped * CurrentReader.TotalTime.TotalSeconds);
        ComputeQueueTimings();
    }

    public void StartPlayback(IAudioSource reader)
    {
        StopPlayback();

        Mixer = new MixingSampleProvider(Format) { ReadFully = true };
        CurrentReader = reader;

        float targetVolume = Volume;
        if (Queue.Count > 0)
        {
            if (Queue[0].PerceivedLoudness < 0)
                System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(Queue[0]));

            targetVolume = CalculateNormalizedVolume(Queue[0], Volume);
            RequestVolumeSliderUpdate?.Invoke(targetVolume);
        }

        var sp = GetSampleProviderCompatible(reader);
        CurrentVolProvider = new VolumeSampleProvider(sp) { Volume = targetVolume };
        Mixer.AddMixerInput(CurrentVolProvider);

        if (CapturingProvider == null)
        {
            // The equalizer sits before the capture tap so visualization, recording
            // and Music Share all see exactly what leaves the speakers.
            EqualizerProcessor = new EqualizerProcessor(Mixer, Equalizer);
            CapturingProvider = new CapturingSampleProvider(EqualizerProcessor);
            CapturingProvider.SamplesCaptured += (_, samples) => AudioSamplesCaptured?.Invoke(this, samples);
        }

        Output = OutputFactory();
        if (OutputDeviceNumber >= 0)
        {
            try { Output.DeviceNumber = OutputDeviceNumber; } catch { }
        }
        Output.Init(CapturingProvider);
        Output.Volume = System.Math.Clamp(AnnouncementOutputFactor * SleepFadeFactor, 0f, 1f);
        Output.PlaybackStopped += Output_PlaybackStopped;
        Output.Play();

        ComputeQueueTimings();
        PlayStateChanged?.Invoke(true);
        NowPlayingChanged?.Invoke(Queue.Count > 0 ? Queue[0] : null);
        EnsureTickTimer();

        var current = Queue.Count > 0 ? Queue[0] : null;
        if (current != null && CurrentReader != null)
            NowPlayingTick?.Invoke(this, new NowPlayingEventArgs { Current = current, CurrentTime = CurrentReader.CurrentTime, TotalTime = CurrentReader.TotalTime });
    }

    public void PlaySong(Song s)
    {
        if (s == null || !System.IO.File.Exists(s.songPath)) return;
        var reader = AudioSourceFactory.Open(s.songPath);
        StartPlayback(reader);
    }

    public void TogglePlayPause()
    {
        if (Output == null)
        {
            if (Queue.Count > 0) PlaySong(Queue[0]);
            return;
        }

        if (Output.PlaybackState == PlaybackState.Playing)
        {
            Output.Pause();
            PlayStateChanged?.Invoke(false);
        }
        else
        {
            Output.Play();
            PlayStateChanged?.Invoke(true);
        }
    }

    public void Next()
    {
        if (Queue.Count <= 1)
        {
            if (Queue.Count == 1) Queue.RemoveAt(0);
            if (AdvanceFromPlaylistQueue()) return;
            StopPlayback();
            QueueEmptied?.Invoke();
            return;
        }

        var nextSong = Queue[1];
        _ = BeginCrossfadeToNext(nextSong);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Previous()
    {
        if (CurrentReader != null) CurrentReader.CurrentTime = TimeSpan.Zero;
        ComputeQueueTimings();
    }

    public void Stop()
    {
        StopPlayback();
        QueueEmptied?.Invoke();
    }

    public void StopPlayback()
    {
        if (Output != null)
        {
            try { Output.PlaybackStopped -= Output_PlaybackStopped; } catch { }
            try { Output.Stop(); } catch { }
            try { Output.Dispose(); } catch { }
            Output = null!;
        }
        if (CurrentReader != null)
        {
            try { CurrentReader.Dispose(); } catch { }
            CurrentReader = null!;
        }
        if (NextReader != null)
        {
            try { NextReader.Dispose(); } catch { }
            NextReader = null!;
        }

        CurrentVolProvider = null!;
        NextVolProvider = null!;
        Mixer = null!;
        EqualizerProcessor = null;
        CapturingProvider = null;

        StopTickTimer();
        PlayStateChanged?.Invoke(false);
        NowPlayingTick?.Invoke(this, new NowPlayingEventArgs());
    }

    private void Output_PlaybackStopped(object? sender, EventArgs e)
    {
        void Handle()
        {
            // The sleep timer is letting this track finish: never advance/repeat
            // into another queue item when the output stops naturally.
            if (SleepTimerHoldsPlayback)
            {
                StopPlayback();
                QueueEmptied?.Invoke();
                return;
            }

            if (RepeatEnabled && Queue.Count > 0)
            {
                try
                {
                    var reader = AudioSourceFactory.Open(Queue[0].songPath);
                    StartPlayback(reader);
                }
                catch { StopPlayback(); QueueEmptied?.Invoke(); }
            }
            else if (!IsCrossfading && Queue.Count > 1)
            {
                _ = AdvanceToNextImmediate();
            }
            else if (PlaylistQueue.Count > 0)
            {
                AdvanceFromPlaylistQueue();
            }
            else
            {
                // Nothing left to play.
                StopPlayback();
                QueueEmptied?.Invoke();
            }
        }

        if (InvokeOnUi != null) InvokeOnUi(Handle);
        else Handle();
    }

    private void RestartCurrent()
    {
        try
        {
            var reader = AudioSourceFactory.Open(Queue[0].songPath);
            StartPlayback(reader);
        }
        catch { StopPlayback(); QueueEmptied?.Invoke(); }
    }

    /// <summary>Called ~10×/s by the host while playback is active. Drives position/tick/crossfade.</summary>
    public void ProcessTick()
    {
        var reader = CurrentReader;
        if (reader == null || IsUserSeeking) return;

        TimeSpan totalTime;
        try { totalTime = reader.TotalTime; } catch { return; }
        if (totalTime.TotalSeconds <= 0) return;

        TimeSpan currentTime;
        try { currentTime = reader.CurrentTime; } catch { return; }
        var remainingTime = totalTime - currentTime;
        if (remainingTime < TimeSpan.Zero) remainingTime = TimeSpan.Zero;

        PositionChanged?.Invoke(System.Math.Clamp(currentTime.TotalSeconds / totalTime.TotalSeconds, 0.0, 1.0), currentTime, remainingTime);

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastSecondaryTickUtc) < TimeSpan.FromMilliseconds(250)) return;
        _lastSecondaryTickUtc = nowUtc;

        // A sleep timer that is letting the last song finish must not crossfade
        // into the next queue item. Stop at the real end; the service fades the
        // final seconds before this point.
        if (SleepTimerHoldsPlayback)
        {
            if (remainingTime.TotalSeconds <= 0.15)
            {
                StopPlayback();
                QueueEmptied?.Invoke();
                return;
            }

            // The timer is waiting for this track to finish. Keep publishing
            // position/play state so the player UI stays live during the wait
            // instead of appearing frozen until the fade completes.
            RaiseNowPlayingTick();
            return;
        }

        // Fill the next queue slot from the playlist *before* the crossfade check.
        // Previously the last queue song hit the "Queue.Count == 1" branch and was
        // replaced only after it had ended, so the playlist song could never
        // crossfade in.
        if (!IsCrossfading && Queue.Count == 1 && PlaylistQueue.Count > 0)
            RefillQueueFromPlaylist();

        double cross = System.Math.Max(0, CrossfadeSeconds);
        var remaining = remainingTime.TotalSeconds;

        if (!IsCrossfading && Queue.Count > 1)
        {
            if (remaining <= cross && remaining > 0.15)
            {
                if (RepeatEnabled) RestartCurrent();
                else _ = BeginCrossfadeToNext(Queue[1]);
            }
            else if (remaining <= 0.15)
            {
                if (RepeatEnabled) RestartCurrent();
                else _ = AdvanceToNextImmediate();
            }
        }
        else if (Queue.Count == 1 && remaining <= 0.15)
        {
            if (RepeatEnabled) { RestartCurrent(); return; }
            if (PlaylistQueue.Count > 0) { AdvanceFromPlaylistQueue(); return; }
            StopPlayback();
            QueueEmptied?.Invoke();
        }

        RaiseNowPlayingTick();
    }

    private void RaiseNowPlayingTick()
    {
        var current = Queue.Count > 0 ? Queue[0] : null;
        if (current == null || CurrentReader == null) return;
        try
        {
            NowPlayingTick?.Invoke(this, new NowPlayingEventArgs
            {
                Current = current,
                CurrentTime = CurrentReader.CurrentTime,
                TotalTime = CurrentReader.TotalTime
            });
        }
        catch { }
    }

    private async System.Threading.Tasks.Task BeginCrossfadeToNext(Song nextSong)
    {
        if (IsCrossfading) return;
        IsCrossfading = true;
        try
        {
            await CrossfadeToNext(nextSong);

            if (Queue.Count > 0) Queue.RemoveAt(0);
            RefillQueueFromPlaylist();

            if (Queue.Count > 0)
                NowPlayingChanged?.Invoke(Queue[0]);

            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
        finally
        {
            IsCrossfading = false;
        }
    }

    private async System.Threading.Tasks.Task CrossfadeToNext(Song nextSong)
    {
        if (CurrentReader == null) return;
        if (!System.IO.File.Exists(nextSong.songPath)) return;

        // Loudness analysis is slow (seconds). It must never delay the fade: start
        // it in the background and use the base volume for this transition. The
        // next transition/poll uses the cached value.
        if (nextSong.PerceivedLoudness < 0)
            _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(nextSong));

        float targetNextVolume = CalculateNormalizedVolume(nextSong, Volume);

        NextReader = AudioSourceFactory.Open(nextSong.songPath);
        var nextSp = GetSampleProviderCompatible(NextReader);
        NextVolProvider = new VolumeSampleProvider(nextSp) { Volume = 0f };

        if (Mixer == null)
        {
            Mixer = new MixingSampleProvider(Format) { ReadFully = true };
            if (CurrentVolProvider != null) Mixer.AddMixerInput(CurrentVolProvider);
        }
        Mixer.AddMixerInput(NextVolProvider);

        double cross = CrossfadeSeconds;
        if (cross <= 0) cross = 0.15;

        float currentStartVolume = CurrentVolProvider?.Volume ?? Volume;
        int steps = 40;
        int delay = System.Math.Max(10, (int)(cross * 1000 / steps));
        bool metadataUpdated = false;
        int halfStep = steps / 2;

        for (int i = 0; i < steps; i++)
        {
            float t = (i + 1f) / steps;
            try { NextVolProvider.Volume = targetNextVolume * t; } catch { }
            try { if (CurrentVolProvider != null) CurrentVolProvider.Volume = currentStartVolume * (1f - t); } catch { }

            if (!metadataUpdated && i >= halfStep)
            {
                metadataUpdated = true;
                NowPlayingChanged?.Invoke(nextSong);
            }

            await Delay(delay);
        }

        RequestVolumeSliderUpdate?.Invoke(targetNextVolume);
        Volume = targetNextVolume;

        var oldProvider = CurrentVolProvider;
        var oldReader = CurrentReader;

        CurrentReader = NextReader;
        CurrentVolProvider = NextVolProvider;
        NextReader = null!;
        NextVolProvider = null!;

        if (Mixer != null && oldProvider != null)
        {
            try { Mixer.RemoveMixerInput(oldProvider); }
            catch
            {
                var newMixer = new MixingSampleProvider(Format) { ReadFully = true };
                newMixer.AddMixerInput(CurrentVolProvider);
                Mixer = newMixer;
                try { Output?.Stop(); } catch { }
                try { Output?.Init(Mixer); } catch { }
                try { Output?.Play(); } catch { }
            }
        }

        if (oldReader != null)
        {
            try { oldReader.Dispose(); } catch { }
        }

        PlayStateChanged?.Invoke(true);
    }

    private async System.Threading.Tasks.Task AdvanceToNextImmediate()
    {
        if (Queue.Count <= 1)
        {
            StopPlayback();
            QueueEmptied?.Invoke();
            return;
        }

        var nextSong = Queue[1];
        try
        {
            if (nextSong.PerceivedLoudness < 0)
                _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(nextSong));

            StopPlayback();

            if (Queue.Count > 0) Queue.RemoveAt(0);
            RefillQueueFromPlaylist();

            var reader = AudioSourceFactory.Open(nextSong.songPath);
            StartPlayback(reader);

            if (Queue.Count > 0) NowPlayingChanged?.Invoke(Queue[0]);
        }
        catch
        {
            StopPlayback();
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    public bool AdvanceFromPlaylistQueue()
    {
        if (PlaylistQueue.Count == 0) return false;

        // Batch the two queue moves plus the two playlist removals into one update,
        // and start playback before the (potentially large) queue payload is built.
        using (BeginBulkUpdate())
        {
            var song = PlaylistQueue[0];
            PlaylistQueue.RemoveAt(0);
            if (Queue.Count > 0) Queue.RemoveAt(0);
            Queue.Add(song);
            if (PlaylistQueue.Count > 0)
            {
                var next = PlaylistQueue[0];
                PlaylistQueue.RemoveAt(0);
                Queue.Add(next);
            }
            ComputeQueueTimings();
            PlaySong(Queue[0]);
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────
    //  Queue management (WPF-free)
    // ──────────────────────────────────────────────────────────────

    public void ComputeQueueTimings()
    {
        TimeSpan initialOffset = TimeSpan.Zero;
        if (CurrentReader != null)
        {
            try { initialOffset = CurrentReader.TotalTime - CurrentReader.CurrentTime; }
            catch { initialOffset = TimeSpan.Zero; }
        }

        TimeSpan cumulative = TimeSpan.Zero;
        if (Queue.Count > 0)
            Queue[0].EstimatedStartDisplay = "Playing Now";

        for (int i = 1; i < Queue.Count; i++)
        {
            var song = Queue[i];
            if (song.Duration == TimeSpan.Zero && System.IO.File.Exists(song.songPath))
            {
                try
                {
                    using var afr = AudioSourceFactory.Open(song.songPath);
                    song.Duration = afr.TotalTime;
                    song.length = TimeDisplay.Clock(song.Duration);
                }
                catch { }
            }

            TimeSpan timeUntil = initialOffset + cumulative;
            song.EstimatedStart = timeUntil;
            song.EstimatedStartDisplay = "Wait Time: " + (timeUntil <= TimeSpan.FromSeconds(1) ? "Now" : TimeDisplay.Clock(timeUntil));
            cumulative += song.Duration;
        }
    }

    public void RemoveQueueItem(int index)
    {
        if (index >= 0 && index < Queue.Count) Queue.RemoveAt(index);
    }

    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Queue.Count) return;
        if (toIndex < 0 || toIndex >= Queue.Count) return;
        if (fromIndex == toIndex) return;
        if (toIndex == 0) return; // never move into now-playing slot
        Queue.Move(fromIndex, toIndex);
    }

    public void ShuffleQueue()
    {
        var rnd = new Random();
        var items = Queue.ToList();
        var nowPlaying = items.Count > 0 ? items[0] : null;
        var remaining = items.Where(s => s != nowPlaying).OrderBy(x => rnd.Next()).ToList();

        using (BeginBulkUpdate())
        {
            Queue.Clear();
            if (nowPlaying != null) Queue.Add(nowPlaying);
            foreach (var song in remaining) Queue.Add(song);
        }
    }

    public void ClearQueue()
    {
        Queue.Clear();
    }

    public void LoadPlaylist(IEnumerable<Song> songs, bool shuffle)
    {
        var ordered = shuffle ? songs.OrderBy(_ => Random.Shared.Next()).ToList() : songs.ToList();

        // One notification for the whole load. The old per-item adds sent hundreds of
        // full queue updates before playback could begin.
        using (BeginBulkUpdate())
        {
            PlaylistQueue.Clear();
            foreach (var s in ordered) PlaylistQueue.Add(s);
        }

        var cfg = ConfigAccessor();
        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
        {
            // Warm the next few tracks only; calculating a 500-song playlist at once
            // would saturate the CPU. Refill/transition warms the rest as needed.
            foreach (var song in ordered.Take(3))
                _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(song));
        }
    }

    public void ClearPlaylistQueue() => PlaylistQueue.Clear();

    public void RemovePlaylistQueueItem(int index)
    {
        if (index >= 0 && index < PlaylistQueue.Count) PlaylistQueue.RemoveAt(index);
    }

    public void MovePlaylistQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= PlaylistQueue.Count) return;
        if (toIndex < 0 || toIndex >= PlaylistQueue.Count) return;
        if (fromIndex == toIndex) return;
        PlaylistQueue.Move(fromIndex, toIndex);
    }

    public void RefillQueueFromPlaylist()
    {
        if (Queue.Count <= 1 && PlaylistQueue.Count > 0)
        {
            var next = PlaylistQueue[0];
            using (BeginBulkUpdate())
            {
                PlaylistQueue.RemoveAt(0);
                Queue.Add(next);
            }

            var cfg = ConfigAccessor();
            if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive &&
                next.PerceivedLoudness < 0)
            {
                _ = System.Threading.Tasks.Task.Run(() => CalculateSongLoudness(next));
            }
        }
    }
}
