using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// WPF-free soundboard engine.
///
/// Owns the soundboard configuration, audio playback (NAudio) and MIDI handling.
/// The Photino web UI drives it through <see cref="YoutubeFormInterop"/> and
/// receives live playback / MIDI events. This replaces the logic that used to
/// live in <c>SoundboardWindow</c> (a WPF Window) so the feature can run without
/// any WPF types.
/// </summary>
public sealed class SoundboardEngine : IDisposable
{
    private static readonly string[] SupportedFormats =
    {
        ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".wma"
    };

    private readonly object _sync = new();
    private readonly SoundboardConfiguration _config;
    private readonly IMidiService _midiService;
    private readonly List<PlaybackInstance> _activePlaybacks = new();
    private readonly AudioClipCache _clipCache = new();
    private readonly Timer _progressTimer;
    private bool _isAssignMode;
    private bool _disposed;
    private bool _clipsPreloaded;
    private int _snapshotBusy;

    /// <summary>Tick interval for the volume fade ramp (~40 updates/s).</summary>
    private const int FadeTickMs = 25;

    private Timer? _fadeTimer;
    private int _fadeTimerRunning;

    /// <summary>
    /// Duration (ms) of the per-button **Fade In** / **Fade Out** volume ramps.
    /// Set to 0 to make the options behave as instant volume changes.
    /// </summary>
    public int FadeDurationMs { get; set; } = 300;

    /// <summary>Creates the output device for a playback (replaced in tests).</summary>
    internal Func<IAudioOutput> OutputFactory { get; set; } = AudioOutputFactory.CreateForSoundboard;

    /// <summary>Opens the audio source for a sound file (replaced in tests).</summary>
    internal Func<string, IAudioSource> SourceFactory { get; set; } = null!;

    /// <summary>Raised ~10×/s while sounds are playing. Payload is a serializable snapshot.</summary>
    public event Action<object>? PlaybackUpdated;

    /// <summary>Raised when a MIDI message is captured during assign mode.</summary>
    public event Action<object>? MidiCaptured;

    /// <summary>Raised when the MIDI layer reports an error.</summary>
    public event Action<string>? MidiError;

    public SoundboardConfiguration Config => _config;
    public bool IsAssignMode => _isAssignMode;
    public static IReadOnlyList<string> SupportedAudioFormats => SupportedFormats;

    public SoundboardEngine()
    {
        _config = SoundboardConfiguration.Load();

        SourceFactory = path => _clipCache.CreateSource(path);

        _midiService = MidiServiceFactory.Create();
        _midiService.MidiMessageReceived += OnMidiMessageReceived;
        _midiService.ErrorOccurred += (_, message) => MidiError?.Invoke(message);

        _progressTimer = new Timer(_ => EmitPlaybackSnapshot(), null, 100, 100);

        if (_config.MidiEnabled)
        {
            EnableMidi();
        }
        else
        {
            UpdateAllMidiFeedback();
        }
    }

    private sealed class PlaybackInstance
    {
        public IAudioOutput? OutputDevice { get; set; }
        public IAudioSource? AudioFile { get; set; }
        public VolumeSampleProvider? VolumeProvider { get; set; }
        public SoundboardButton Data { get; set; } = null!;
        public int Row { get; set; }
        public int Col { get; set; }
        public string PageId { get; set; } = string.Empty;
        public bool IsLooping { get; set; }
        public volatile bool IsManuallyStopping;

        /// <summary>Steady-state volume (button volume × master volume, 0..3).</summary>
        public float TargetVolume { get; set; } = 1f;

        // Active fade-in / fade-out ramp (advanced by <see cref="TickFades"/>).
        public float FadeFrom { get; set; }
        public float FadeTo { get; set; }
        public int FadeDurationMs { get; set; }
        public DateTime FadeStartUtc { get; set; }
        public bool IsFadingOut { get; set; }
        public bool FadeActive => FadeDurationMs > 0;
    }

    // ──────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────

    public object BuildState()
    {
        PreloadClips();

        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            var buttons = new List<object>();
            for (int i = 0; i < page.TotalSlots; i++)
            {
                int row = i / page.Columns;
                int col = i % page.Columns;
                buttons.Add(ButtonData(page.GetButton(row, col), row, col, i));
            }

            var pages = _config.Pages
                .Select((p, idx) => new { index = idx, id = p.Id, name = p.Name, columns = p.Columns, rows = p.Rows })
                .ToList();

            return new
            {
                pages,
                currentPageIndex = _config.CurrentPageIndex,
                currentPage = new { id = page.Id, name = page.Name, columns = page.Columns, rows = page.Rows },
                buttons,
                masterVolume = _config.MasterVolume,
                outputDeviceNumber = _config.OutputDeviceNumber,
                audioDevices = GetAudioDevices(),
                midiEnabled = _config.MidiEnabled,
                midiInputDevice = _config.MidiInputDevice,
                midiOutputDevice = _config.MidiOutputDevice,
                midiInputs = GetMidiInputs(),
                midiOutputs = GetMidiOutputs(),
                emptyFeedbackVelocity = ParseVelocityFromColor(_config.EmptyButtonFeedbackColor),
                keyboardShortcuts = new
                {
                    stopAll = ShortcutData(_config.KeyboardShortcuts?.StopAll),
                    volumeUp = ShortcutData(_config.KeyboardShortcuts?.VolumeUp),
                    volumeDown = ShortcutData(_config.KeyboardShortcuts?.VolumeDown),
                    nextPage = ShortcutData(_config.KeyboardShortcuts?.NextPage),
                    previousPage = ShortcutData(_config.KeyboardShortcuts?.PreviousPage)
                },
                supportedFormats = SupportedFormats,
                isAssignMode = _isAssignMode
            };
        }
    }

    private void PreloadClips()
    {
        if (_clipsPreloaded) return;
        _clipsPreloaded = true;
        try
        {
            var folder = SoundboardConfiguration.GetSoundboardFolder();
            foreach (var page in _config.Pages)
            {
                foreach (var button in page.Buttons)
                {
                    if (button.IsEnabled && !string.IsNullOrWhiteSpace(button.SoundFile))
                        _clipCache.Preload(Path.Combine(folder, button.SoundFile));
                }
            }
        }
        catch { }
    }

    private static object ButtonData(SoundboardButton b, int row, int col, int index) => new    {
        index,
        row,
        col,
        name = b.Name,
        icon = b.Icon,
        color = b.Color,
        length = b.Length,
        volume = b.Volume,
        repeatMode = string.IsNullOrWhiteSpace(b.RepeatMode) ? "none" : b.RepeatMode,
        fadeIn = b.FadeIn,
        fadeOut = b.FadeOut,
        soundFile = b.SoundFile ?? string.Empty,
        isEmpty = b.IsEmpty,
        customOutputDeviceNumber = b.CustomOutputDeviceNumber,
        midi = b.MidiMapping == null || !b.MidiMapping.IsConfigured ? null : new
        {
            channel = b.MidiMapping.Channel,
            note = b.MidiMapping.Note,
            messageType = b.MidiMapping.MessageType,
            velocityPressed = b.MidiMapping.VelocityPressed,
            velocityUnpressed = b.MidiMapping.VelocityUnpressed
        },
        shortcut = b.KeyboardShortcut == null ? null : new
        {
            enabled = b.KeyboardShortcut.Enabled,
            gesture = b.KeyboardShortcut.Gesture ?? string.Empty
        }
    };

    private static object ShortcutData(KeyboardShortcutConfig? s) => new
    {
        enabled = s?.Enabled == true && !string.IsNullOrWhiteSpace(s.Gesture),
        gesture = s?.Gesture ?? string.Empty
    };

    public static List<object> GetAudioDevices()
    {
        var list = new List<object> { new { number = -1, name = "Default Audio Device" } };
        foreach (var d in AudioDevices.Enumerate())
            list.Add(new { number = d.Number, name = d.Name });
        return list;
    }

    private List<object> GetMidiInputs() =>
        _midiService.GetInputDevices().Select(d => (object)new { number = d.DeviceNumber, name = d.Name }).ToList();

    private List<object> GetMidiOutputs() =>
        _midiService.GetOutputDevices().Select(d => (object)new { number = d.DeviceNumber, name = d.Name }).ToList();

    // ──────────────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _progressTimer.Dispose(); } catch { }
        StopFadeTimer();
        try { _fadeTimer?.Dispose(); } catch { }
        _fadeTimer = null;
        StopAll(allowFadeOut: false);
        try
        {
            _midiService.MidiMessageReceived -= OnMidiMessageReceived;
            _midiService.Dispose();
        }
        catch { }
    }

    // ──────────────────────────────────────────────────────────────
    //  Playback
    // ──────────────────────────────────────────────────────────────

    public object PlayButton(int row, int col)
    {
        SoundboardButton data;
        string soundFile;
        string pageId;
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            if (row < 0 || col < 0 || row >= page.Rows || col >= page.Columns)
                return new { error = "Button out of range" };
            data = page.GetButton(row, col);
            if (data.IsEmpty) return new { error = "Empty button" };
            soundFile = Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), data.SoundFile);
            pageId = page.Id;
        }

        if (!File.Exists(soundFile))
            return new { error = "Sound file not found: " + data.SoundFile };

        var repeatMode = string.IsNullOrWhiteSpace(data.RepeatMode) ? "none" : data.RepeatMode;

        PlaybackInstance? existing;
        lock (_sync)
        {
            existing = _activePlaybacks.FirstOrDefault(p =>
                p.Row == row && p.Col == col && p.PageId == pageId &&
                p.Data.SoundFile == data.SoundFile);
        }

        if (existing != null)
        {
            switch (repeatMode)
            {
                case "overlay":
                    StartNewPlayback(data, row, col, pageId, soundFile, false);
                    break;
                default: // none / loop => stop on re-click
                    StopPlayback(existing);
                    break;
            }
        }
        else
        {
            StartNewPlayback(data, row, col, pageId, soundFile, repeatMode == "loop");
        }

        EmitPlaybackSnapshot(force: true);
        return new { success = true };
    }

    private void StartNewPlayback(SoundboardButton data, int row, int col, string pageId, string soundFile, bool shouldLoop)
    {
        try
        {
            var audioFile = SourceFactory(soundFile);
            var targetDevice = data.CustomOutputDeviceNumber ?? _config.OutputDeviceNumber;
            var outputDevice = OutputFactory();
            outputDevice.DeviceNumber = targetDevice;
            var volumeProvider = new VolumeSampleProvider(audioFile.ToSampleProvider());
            outputDevice.Init((ISampleProvider)volumeProvider);
            outputDevice.Play();

            var playback = new PlaybackInstance
            {
                OutputDevice = outputDevice,
                AudioFile = audioFile,
                VolumeProvider = volumeProvider,
                Data = data,
                Row = row,
                Col = col,
                PageId = pageId,
                IsLooping = shouldLoop
            };

            // Fade in from silence when the button asks for it; otherwise jump to
            // the target volume.
            if (data.FadeIn && FadeDurationMs > 0)
                BeginFade(playback, 0f, TargetVolumeFor(playback), FadeDurationMs, fadeOut: false);
            else
                ApplyVolume(playback);

            outputDevice.PlaybackStopped += (_, _) =>
            {
                if (playback.IsManuallyStopping) return;
                if (playback.IsLooping && playback.AudioFile != null)
                {
                    try
                    {
                        playback.AudioFile.Position = 0;
                        playback.OutputDevice?.Play();

                        // Re-apply the fade-in for each loop iteration.
                        if (playback.Data.FadeIn && FadeDurationMs > 0)
                            BeginFade(playback, 0f, TargetVolumeFor(playback), FadeDurationMs, fadeOut: false);
                    }
                    catch { }
                }
                else
                {
                    // Natural end of the clip: it played in full, so don't fade it out.
                    StopPlayback(playback, allowFadeOut: false);
                }
            };

            lock (_sync) { _activePlaybacks.Add(playback); }
            UpdateMidiFeedback(data, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Soundboard] Playback error: {ex.Message}");
        }
    }

    /// <summary>Computes and remembers the steady-state volume for a playback.</summary>
    private float TargetVolumeFor(PlaybackInstance playback)
    {
        float target = Math.Clamp(playback.Data.Volume * _config.MasterVolume, 0f, 3f);
        playback.TargetVolume = target;
        return target;
    }

    private void ApplyVolume(PlaybackInstance playback)
    {
        try
        {
            if (playback.VolumeProvider == null) return;

            float target = TargetVolumeFor(playback);

            // While a fade is running, retarget its end value instead of jumping.
            if (playback.FadeActive)
            {
                playback.FadeTo = target;
                return;
            }

            playback.VolumeProvider.Volume = target;
        }
        catch { }
    }

    private void StopPlayback(PlaybackInstance playback, bool allowFadeOut = true)
    {
        if (playback == null) return;

        // Fade out (per-button option) before tearing the output down.
        if (allowFadeOut && playback.Data.FadeOut && FadeDurationMs > 0 && !playback.IsFadingOut)
        {
            // Suppress the loop restart / natural-end handler while fading out.
            playback.IsManuallyStopping = true;
            BeginFade(playback, playback.VolumeProvider?.Volume ?? playback.TargetVolume, 0f, FadeDurationMs, fadeOut: true);
            return; // TickFades stops the playback when the ramp completes
        }

        bool anyRemaining;
        try
        {
            playback.IsManuallyStopping = true;
            playback.FadeDurationMs = 0;
            playback.OutputDevice?.Stop();
            playback.OutputDevice?.Dispose();
            playback.AudioFile?.Dispose();
        }
        catch { }
        finally
        {
            lock (_sync)
            {
                _activePlaybacks.Remove(playback);
                anyRemaining = _activePlaybacks.Any(p =>
                    p.Row == playback.Row && p.Col == playback.Col && p.PageId == playback.PageId);
            }
            if (!anyRemaining) UpdateMidiFeedback(playback.Data, false);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Volume fades (per-button Fade In / Fade Out)
    //
    //  The ramp is driven by elapsed wall-clock time, so a lagging tick never
    //  makes the fade short or steppy. A single timer is started on the first
    //  fade and stops itself once no playback is fading.
    // ──────────────────────────────────────────────────────────────

    private void BeginFade(PlaybackInstance playback, float from, float to, int durationMs, bool fadeOut)
    {
        playback.FadeFrom = Math.Clamp(from, 0f, 3f);
        playback.FadeTo = Math.Clamp(to, 0f, 3f);
        playback.FadeDurationMs = Math.Max(0, durationMs);
        playback.FadeStartUtc = DateTime.UtcNow;
        playback.IsFadingOut = fadeOut;

        TrySetVolume(playback, playback.FadeFrom);

        if (playback.FadeDurationMs == 0)
        {
            TrySetVolume(playback, playback.FadeTo);
            return;
        }

        EnsureFadeTimer();
    }

    private static void TrySetVolume(PlaybackInstance playback, float volume)
    {
        try
        {
            if (playback.VolumeProvider != null)
                playback.VolumeProvider.Volume = Math.Clamp(volume, 0f, 3f);
        }
        catch { }
    }

    private void EnsureFadeTimer()
    {
        if (Interlocked.CompareExchange(ref _fadeTimerRunning, 1, 0) != 0) return;
        _fadeTimer = new Timer(_ => TickFades(), null, FadeTickMs, FadeTickMs);
    }

    private void TickFades()
    {
        if (_disposed) return;

        List<PlaybackInstance>? finishedFadeOuts = null;
        bool fadesRemain = false;

        lock (_sync)
        {
            foreach (var playback in _activePlaybacks)
            {
                if (!playback.FadeActive) continue;

                var elapsed = (DateTime.UtcNow - playback.FadeStartUtc).TotalMilliseconds;
                var progress = Math.Clamp(elapsed / playback.FadeDurationMs, 0.0, 1.0);
                TrySetVolume(playback, (float)(playback.FadeFrom + (playback.FadeTo - playback.FadeFrom) * progress));

                if (progress >= 1.0)
                {
                    playback.FadeDurationMs = 0;
                    if (playback.IsFadingOut) (finishedFadeOuts ??= new List<PlaybackInstance>()).Add(playback);
                }
                else
                {
                    fadesRemain = true;
                }
            }
        }

        if (finishedFadeOuts != null)
        {
            foreach (var playback in finishedFadeOuts) StopPlayback(playback, allowFadeOut: false);
        }

        if (!fadesRemain) StopFadeTimer();
    }

    private void StopFadeTimer()
    {
        var timer = Interlocked.Exchange(ref _fadeTimer, null);
        if (timer != null)
        {
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            try { timer.Dispose(); } catch { }
        }

        Volatile.Write(ref _fadeTimerRunning, 0);

        // A fade may have started while this tick was finishing; make sure it is
        // still being driven.
        if (HasActiveFades()) EnsureFadeTimer();
    }

    private bool HasActiveFades()
    {
        lock (_sync) return _activePlaybacks.Any(p => p.FadeActive);
    }

    public object StopAll() => StopAll(allowFadeOut: true);

    private object StopAll(bool allowFadeOut)
    {
        List<PlaybackInstance> copy;
        lock (_sync) { copy = _activePlaybacks.ToList(); }
        foreach (var p in copy) StopPlayback(p, allowFadeOut);
        EmitPlaybackSnapshot(force: true);
        return new { success = true };
    }

    public object SetMasterVolume(double volume)
    {
        lock (_sync)
        {
            _config.MasterVolume = (float)Math.Clamp(volume, 0.0, 3.0);
            foreach (var p in _activePlaybacks) ApplyVolume(p);
            _config.Save();
        }
        return new { success = true, masterVolume = _config.MasterVolume };
    }

    public object SetOutputDevice(int deviceNumber)
    {
        lock (_sync)
        {
            _config.OutputDeviceNumber = deviceNumber;
            _config.Save();
        }
        return new { success = true };
    }

    private bool _hadActiveSnapshots;

    private void EmitPlaybackSnapshot(bool force = false)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _snapshotBusy, 1) == 1) return;
        try
        {
            object? payload = null;
            int count;
            lock (_sync)
            {
                var page = _config.GetCurrentPage();
                var items = new List<object>();
                foreach (var p in _activePlaybacks)
                {
                    if (p.PageId != page.Id) continue;
                    double elapsed = 0, total = 0;
                    try
                    {
                        if (p.AudioFile != null)
                        {
                            elapsed = p.AudioFile.CurrentTime.TotalSeconds;
                            total = p.AudioFile.TotalTime.TotalSeconds;
                        }
                    }
                    catch { }
                    var progress = total > 0 ? Math.Clamp(elapsed / total, 0, 1) : 0;
                    items.Add(new { row = p.Row, col = p.Col, elapsed, total, progress });
                }
                count = items.Count;
                payload = new { pageId = page.Id, items };
            }

            // Avoid spamming the UI when nothing is playing (but always emit the
            // final "cleared" snapshot so buttons reset).
            if (count == 0 && !_hadActiveSnapshots && !force)
            {
                return;
            }
            _hadActiveSnapshots = count > 0;
            PlaybackUpdated?.Invoke(payload);
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _snapshotBusy, 0);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Pages
    // ──────────────────────────────────────────────────────────────

    public object SetCurrentPage(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _config.Pages.Count) return new { error = "Invalid page" };
            _config.CurrentPageIndex = index;
            _config.Save();
        }
        UpdateAllMidiFeedback();
        return new { success = true };
    }

    public object StepPage(int delta)
    {
        lock (_sync)
        {
            var target = Math.Clamp(_config.CurrentPageIndex + delta, 0, _config.Pages.Count - 1);
            _config.CurrentPageIndex = target;
            _config.Save();
        }
        UpdateAllMidiFeedback();
        return new { success = true, currentPageIndex = _config.CurrentPageIndex };
    }

    public object AddPage(string name, int columns, int rows)
    {
        lock (_sync)
        {
            _config.AddPage(string.IsNullOrWhiteSpace(name) ? "Untitled Page" : name,
                Math.Clamp(columns, 1, 12), Math.Clamp(rows, 1, 12));
        }
        return new { success = true };
    }

    public object DeletePage(int index)
    {
        lock (_sync)
        {
            if (_config.Pages.Count <= 1) return new { error = "Cannot delete the last page" };
            if (!_config.DeletePage(index)) return new { error = "Invalid page" };
        }
        UpdateAllMidiFeedback();
        return new { success = true };
    }

    public object RenamePage(int index, string name)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _config.Pages.Count) return new { error = "Invalid page" };
            if (string.IsNullOrWhiteSpace(name)) return new { error = "Name required" };
            _config.Pages[index].Name = name;
            _config.Save();
        }
        return new { success = true };
    }

    public object MovePage(int index, int delta)
    {
        lock (_sync)
        {
            var target = index + delta;
            if (index < 0 || index >= _config.Pages.Count || target < 0 || target >= _config.Pages.Count)
                return new { error = "Invalid move" };
            _config.MovePage(index, target);
        }
        return new { success = true, currentPageIndex = _config.CurrentPageIndex };
    }

    public object SetGridSize(int columns, int rows)
    {
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            page.Columns = Math.Clamp(columns, 1, 12);
            page.Rows = Math.Clamp(rows, 1, 12);
            _config.Save();
        }
        UpdateAllMidiFeedback();
        return new { success = true };
    }

    // ──────────────────────────────────────────────────────────────
    //  Buttons
    // ──────────────────────────────────────────────────────────────

    /// <summary>Copies a file into the soundboard folder (if needed) and returns its file name.</summary>
    public static string CopyIntoSoundboardFolder(string sourcePath)
    {
        var folder = SoundboardConfiguration.GetSoundboardFolder();
        var fileName = Path.GetFileName(sourcePath);
        var dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest)) File.Copy(sourcePath, dest, false);
        return fileName;
    }

    public static bool IsSupportedAudioFile(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return !string.IsNullOrEmpty(ext) && SupportedFormats.Contains(ext);
    }

    public object UpdateButton(
        int row, int col, string name, string icon, string color, string soundFile,
        bool fadeIn, bool fadeOut, string repeatMode, double volume,
        int? customDevice, bool shortcutEnabled, string shortcutGesture)
    {
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            var data = page.GetButton(row, col);

            data.Name = name ?? data.Name;
            data.Icon = icon ?? data.Icon;
            data.Color = color ?? data.Color;
            data.FadeIn = fadeIn;
            data.FadeOut = fadeOut;
            data.RepeatMode = string.IsNullOrWhiteSpace(repeatMode) ? "none" : repeatMode;
            data.Volume = (float)Math.Clamp(volume, 0.0, 3.0);
            data.CustomOutputDeviceNumber = customDevice;

            if (!string.IsNullOrWhiteSpace(soundFile))
            {
                data.SoundFile = Path.GetFileName(soundFile);
                var full = Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), data.SoundFile);
                if (File.Exists(full))
                {
                    data.Length = ReadAudioDuration(full);
                    _clipCache.Preload(full);
                }
                data.IsEnabled = true;
            }
            data.KeyboardShortcut = string.IsNullOrWhiteSpace(shortcutGesture)
                ? null
                : new KeyboardShortcutConfig { Enabled = shortcutEnabled, Gesture = shortcutGesture };

            page.SetButton(row, col, data);
            _config.Save();
        }
        return new { success = true };
    }

    public object DeleteButton(int row, int col)
    {
        string soundFile;
        SoundboardButton data;
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            data = page.GetButton(row, col);
            soundFile = data.SoundFile;

            if (data.MidiMapping != null) UpdateMidiFeedback(data, false);

            data.Name = "Empty";
            data.SoundFile = string.Empty;
            data.Icon = "🔊";
            data.Color = "#2A2A2A";
            data.Length = 0;
            data.FadeIn = false;
            data.FadeOut = false;
            data.RepeatMode = "none";
            data.IsEnabled = false;
            data.CustomOutputDeviceNumber = null;
            data.KeyboardShortcut = null;
            data.MidiMapping = null;
            page.SetButton(row, col, data);
            _config.Save();

            // Stop any active playback for this button (the file is about to be deleted).
            var active = _activePlaybacks.Where(p => p.PageId == page.Id && p.Row == row && p.Col == col).ToList();
            foreach (var p in active) StopPlayback(p, allowFadeOut: false);
        }

        // Delete file if no other button references it
        if (!string.IsNullOrEmpty(soundFile) && !IsFileUsedElsewhere(soundFile))
        {
            try
            {
                var path = Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), soundFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
        return new { success = true };
    }

    private bool IsFileUsedElsewhere(string soundFile)
    {
        lock (_sync)
        {
            foreach (var page in _config.Pages)
            {
                foreach (var b in page.Buttons)
                {
                    if (!b.IsEmpty && string.Equals(b.SoundFile, soundFile, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    public object ClearMidiMapping(int row, int col)
    {
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            var data = page.GetButton(row, col);
            if (data.MidiMapping != null) UpdateMidiFeedback(data, false);
            data.MidiMapping = null;
            page.SetButton(row, col, data);
            _config.Save();
        }
        return new { success = true };
    }

    public object SetButtonMidi(int row, int col, MidiMapping? mapping)
    {
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            var data = page.GetButton(row, col);
            data.MidiMapping = mapping;
            page.SetButton(row, col, data);
            _config.Save();
        }
        _isAssignMode = false;
        UpdateAllMidiFeedback();
        return new { success = true };
    }

    public static double ReadAudioDuration(string path)
    {
        try
        {
            using var audio = AudioSourceFactory.Open(path);
            return audio.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  MIDI
    // ──────────────────────────────────────────────────────────────

    public object SetMidiEnabled(bool enabled)
    {
        lock (_sync)
        {
            _config.MidiEnabled = enabled;
            _config.Save();
        }
        if (enabled) EnableMidi(); else DisableMidi();
        return new { success = true };
    }

    public object SetMidiDevices(int inputDevice, int outputDevice)
    {
        lock (_sync)
        {
            _config.MidiInputDevice = inputDevice;
            _config.MidiOutputDevice = outputDevice;
            _config.Save();
        }
        if (_config.MidiEnabled) EnableMidi();
        return new { success = true };
    }

    public object SetAssignMode(bool enabled)
    {
        _isAssignMode = enabled;
        return new { success = true, isAssignMode = _isAssignMode };
    }

    /// <summary>Ensures assign mode is active so the next MIDI message is captured.</summary>
    public object StartMidiAssign()
    {
        _isAssignMode = true;
        return new { success = true, isAssignMode = true };
    }

    public object SetEmptyFeedbackVelocity(int velocity)
    {
        lock (_sync)
        {
            _config.EmptyButtonFeedbackColor = VelocityToColor(Math.Clamp(velocity, 0, 127));
            _config.Save();
        }
        UpdateAllMidiFeedback();
        return new { success = true };
    }

    public object TestMidiFeedback()
    {
        if (!_config.MidiEnabled || _midiService.OutputDeviceNumber == null)
            return new { error = "Please enable MIDI and select an output device first." };

        for (int note = 0; note < 128; note++) _midiService.SendNoteOn(1, note, 64);
        _ = System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
        {
            for (int note = 0; note < 128; note++) _midiService.SendNoteOff(1, note);
        });
        return new { success = true };
    }

    private void EnableMidi()
    {
        try
        {
            _midiService.IsEnabled = true;
            if (_config.MidiInputDevice >= 0) _midiService.ConnectInput(_config.MidiInputDevice);
            if (_config.MidiOutputDevice >= 0) _midiService.ConnectOutput(_config.MidiOutputDevice);
            UpdateAllMidiFeedback();
        }
        catch (Exception ex)
        {
            MidiError?.Invoke("Failed to enable MIDI: " + ex.Message);
        }
    }

    private void DisableMidi()
    {
        try
        {
            _midiService.IsEnabled = false;
            _midiService.DisconnectDevices();
        }
        catch { }
    }

    private void OnMidiMessageReceived(object? sender, MidiMessage message)
    {
        if (_isAssignMode)
        {
            var parsed = MidiEventParser.Parse(message);
            if (parsed != null) MidiCaptured?.Invoke(parsed);
            return;
        }

        if (!_config.MidiEnabled) return;

        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            for (int i = 0; i < page.TotalSlots; i++)
            {
                int row = i / page.Columns;
                int col = i % page.Columns;
                var data = page.GetButton(row, col);
                var m = data.MidiMapping;
                if (m == null || !m.IsConfigured) continue;
                if (data.IsEmpty) continue;

                bool matched;
                if (message.Type == MidiMessageType.NoteOn)
                {
                    matched = m.MessageType == "NoteOn" && m.Channel == message.Channel &&
                              m.Note == message.Data1 && message.Data2 > 0;
                }
                else if (message.Type == MidiMessageType.ControlChange)
                {
                    matched = m.MessageType == "ControlChange" && m.Channel == message.Channel &&
                              m.Note == message.Data1 && message.Data2 > 0;
                }
                else
                {
                    matched = false;
                }

                if (matched)
                {
                    _ = PlayButton(row, col);
                    break;
                }
            }
        }
    }

    private void UpdateMidiFeedback(SoundboardButton data, bool isPressed)
    {
        if (!_config.MidiEnabled || _midiService.OutputDeviceNumber == null) return;
        var m = data.MidiMapping;
        if (m == null || !m.IsConfigured) return;

        try
        {
            int velocity = isPressed ? m.VelocityPressed : m.VelocityUnpressed;
            if (m.MessageType == "NoteOn")
            {
                if (velocity > 0) _midiService.SendNoteOn(m.Channel, m.Note, velocity);
                else _midiService.SendNoteOff(m.Channel, m.Note);
            }
            else if (m.MessageType == "ControlChange")
            {
                _midiService.SendControlChange(m.Channel, m.Note, velocity);
            }
        }
        catch { }
    }

    private void UpdateAllMidiFeedback()
    {
        if (!_config.MidiEnabled) return;

        List<(SoundboardButton data, bool playing)> snapshot = new();
        lock (_sync)
        {
            var page = _config.GetCurrentPage();
            for (int i = 0; i < page.TotalSlots; i++)
            {
                int row = i / page.Columns;
                int col = i % page.Columns;
                var data = page.GetButton(row, col);
                if (data.MidiMapping == null || !data.MidiMapping.IsConfigured) continue;

                if (data.IsEmpty)
                {
                    var m = data.MidiMapping;
                    try
                    {
                        int emptyVelocity = ParseVelocityFromColor(_config.EmptyButtonFeedbackColor);
                        if (m.MessageType == "NoteOn") _midiService.SendNoteOn(m.Channel, m.Note, emptyVelocity);
                        else if (m.MessageType == "ControlChange") _midiService.SendControlChange(m.Channel, m.Note, emptyVelocity);
                    }
                    catch { }
                }
                else
                {
                    bool playing = _activePlaybacks.Any(p =>
                        p.PageId == page.Id && p.Row == row && p.Col == col);
                    snapshot.Add((data, playing));
                }
            }
        }
        foreach (var (data, playing) in snapshot) UpdateMidiFeedback(data, playing);
    }

    private static int ParseVelocityFromColor(string color)
    {
        try
        {
            if (string.IsNullOrEmpty(color) || !color.StartsWith("#")) return 10;
            var hex = color.Substring(1);
            if (hex.Length >= 2)
            {
                int brightness = Convert.ToInt32(hex.Substring(0, 2), 16);
                return brightness * 127 / 255;
            }
        }
        catch { }
        return 10;
    }

    private static string VelocityToColor(int velocity)
    {
        int brightness = velocity * 255 / 127;
        return $"#{brightness:X2}{brightness:X2}{brightness:X2}";
    }

    // ──────────────────────────────────────────────────────────────
    //  Keyboard shortcuts config
    // ──────────────────────────────────────────────────────────────

    public object SaveKeyboardShortcuts(object shortcuts)
    {
        if (shortcuts is not Newtonsoft.Json.Linq.JObject o) return new { error = "Invalid payload" };
        lock (_sync)
        {
            _config.KeyboardShortcuts ??= new GlobalKeyboardShortcuts();
            ApplyShortcut(_config.KeyboardShortcuts.StopAll, o["stopAll"]);
            ApplyShortcut(_config.KeyboardShortcuts.VolumeUp, o["volumeUp"]);
            ApplyShortcut(_config.KeyboardShortcuts.VolumeDown, o["volumeDown"]);
            ApplyShortcut(_config.KeyboardShortcuts.NextPage, o["nextPage"]);
            ApplyShortcut(_config.KeyboardShortcuts.PreviousPage, o["previousPage"]);
            _config.Save();
        }
        return new { success = true };
    }

    private static void ApplyShortcut(KeyboardShortcutConfig target, Newtonsoft.Json.Linq.JToken? token)
    {
        if (token is not Newtonsoft.Json.Linq.JObject o) return;
        target.Gesture = o["gesture"]?.ToString() ?? string.Empty;
        target.Enabled = (o["enabled"]?.ToObject<bool>() ?? false) && !string.IsNullOrWhiteSpace(target.Gesture);
    }
}
