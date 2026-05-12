using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Generic;
using CefSharp.DevTools.HeapProfiler;
using TagLib;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite
{
    public partial class MusicPlayer : Window
    {
        public ObservableCollection<Song> Queue { get; } = new ObservableCollection<Song>();

        // For overlapping crossfade we use MixingSampleProvider
        private WaveOutEvent _outputDevice;
        private MixingSampleProvider _mixer;
        private AudioFileReader _currentReader;
        private AudioFileReader _nextReader;

        // Use VolumeSampleProvider wrappers so volume ramps don't touch the underlying reader.Volume which can cause glitches
        private VolumeSampleProvider _currentVolProvider;
        private VolumeSampleProvider _nextVolProvider;

        private float _volume = 0.9f;
        private DispatcherTimer _positionTimer;
        private bool _isUserSeeking = false;
        private WaveFormat _format;

        // flags
        private bool _isCrossfading = false;
        private bool _isUserAdjustingVolume = false; // Track if user is manually changing volume
        private double _targetLoudness = -14.0; // Target loudness in LUFS
        private readonly SemaphoreSlim _announcementVolumeGate = new SemaphoreSlim(1, 1);
        private float _announcementOutputFactor = 1f;
        private DateTime _lastSecondaryPlaybackTickUtc = DateTime.MinValue;

        // Audio capture for Music Share
        private CapturingSampleProvider? _capturingProvider;
        public event EventHandler<float[]>? AudioSamplesCaptured;

        // Events for external presentation display
        public event EventHandler<NowPlayingEventArgs> NowPlayingTick;
        public event EventHandler QueueChanged;

        public class NowPlayingEventArgs : EventArgs
        {
            public Song Current { get; set; }
            public TimeSpan CurrentTime { get; set; }
            public TimeSpan TotalTime { get; set; }
        }

        private LyricsService _lyricsService = new LyricsService();
        private List<(TimeSpan Time, string Text)> _currentSyncedLines = new List<(TimeSpan, string)>();
        private TextBlock _currentHighlighted = null;
        private const double BaseLyricFontSize = 30;
        private const double ActiveLyricFontSize = 38;
        private const int LyricAnimationDurationMs = 230;
        private static readonly Color InactiveLyricColor = Color.FromRgb(195, 195, 195);
        private static readonly Color ActiveLyricColor = Colors.White;
        private const double InactiveLyricOpacity = 0.82;
        private const double ActiveLyricOpacity = 1.0;
        private readonly object _lyricsCacheLock = new object();
        private readonly Dictionary<string, LyricsResult> _lyricsCache = new Dictionary<string, LyricsResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<LyricsResult>> _lyricsInFlight = new Dictionary<string, Task<LyricsResult>>(StringComparer.OrdinalIgnoreCase);
        private int _lyricsRequestVersion;
        private string _currentLyricsSongKey = string.Empty;
        private bool _isNoLyricsLayout;
        private int _layoutTransitionVersion;
        private AnnouncementWindow? _announcementWindow = null;
        private MusicShare? _musicShareWindow = null;

        // Visualization window
        private VisualizationWindow? _visualizationWindow = null;

        // Crash-recovery state persistence
        private DispatcherTimer _stateTimer;

        private class SongSnapshot
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string SongPath { get; set; } = "";
            public double DurationSeconds { get; set; }
            public double PerceivedLoudness { get; set; } = -1;
        }

        private class PlayerSnapshot
        {
            public List<SongSnapshot> Songs { get; set; } = new();
            public double PlaybackPositionSeconds { get; set; }
            public float Volume { get; set; }
            public DateTime SavedAt { get; set; }
        }

        public static readonly DependencyProperty SmoothScrollOffsetProperty =
            DependencyProperty.RegisterAttached(
                "SmoothScrollOffset",
                typeof(double),
                typeof(MusicPlayer),
                new PropertyMetadata(0.0, OnSmoothScrollOffsetChanged));

        private static void OnSmoothScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        private static string GetPlayerStatePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appData, "SongRequestDesktopV2Rewrite");
            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "player_state.json");
        }

        public MusicPlayer()
        {
            InitializeComponent();

            QueueList.ItemsSource = Queue;

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _positionTimer.Tick += PositionTimer_Tick;

            _format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

            // populate output devices
            try
            {
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    OutputDeviceCombo.Items.Add(WaveOut.GetCapabilities(i).ProductName);
                }
                if (OutputDeviceCombo.Items.Count > 0) OutputDeviceCombo.SelectedIndex = 0;
            }
            catch
            {
                // ignore if no devices or NAudio can't enumerate in this environment
            }

            // when queue changes, recompute ETA / playback times
            Queue.CollectionChanged += (s, e) =>
            {
                ComputeQueueTimings();
                QueueChanged?.Invoke(this, EventArgs.Empty);
                PrefetchNextLyrics();
            };

            // Setup normalize volume button visibility
            UpdateNormalizeVolumeButtonVisibility();
            UpdateAnnouncementButtonVisibility();
            UpdateNormalizationStatus();
            UpdateAutopilotBadge();

            ConfigService.Instance.Current.PropertyChanged += Config_PropertyChanged;

            // Start 2-minute auto-save timer for crash recovery
            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _stateTimer.Tick += (s, e) => SavePlayerState();
            _stateTimer.Start();

            Loaded += MusicPlayer_Loaded;
            SizeChanged += MusicPlayer_SizeChanged;
            Player.SizeChanged += MusicPlayer_SizeChanged;
        }

        private void MusicPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                ApplyLyricsAdaptiveLayoutValues(_isNoLyricsLayout, false);
            }), DispatcherPriority.Render);
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(Config.NormalizeVolume))
                {
                    UpdateNormalizeVolumeButtonVisibility();
                    UpdateNormalizationStatus();

                    // If normalization is turned off in settings, deactivate it
                    var cfg = ConfigService.Instance.Current;
                    if (cfg != null && !cfg.NormalizeVolume)
                    {
                        cfg.NormalizationActive = false;
                    }
                }
                else if (e.PropertyName == nameof(Config.NormalizationActive))
                {
                    UpdateNormalizationStatus();

                    // If normalization was just activated/deactivated during playback, adjust volume immediately
                    var cfg = ConfigService.Instance.Current;
                    if (cfg != null && _currentReader != null && Queue.Count > 0)
                    {
                        float targetVolume;
                        if (cfg.NormalizationActive && cfg.NormalizeVolume && cfg.DefaultVolume >= 0)
                        {
                            // Calculate and apply normalized volume
                            targetVolume = CalculateNormalizedVolume(Queue[0], cfg.DefaultVolume);
                            System.Diagnostics.Debug.WriteLine($"Normalization activated - adjusting volume to {targetVolume:F2}");
                        }
                        else
                        {
                            // Use current slider value
                            targetVolume = (float)VolumeSlider.Value;
                            System.Diagnostics.Debug.WriteLine($"Normalization deactivated - keeping current volume {targetVolume:F2}");
                        }

                        // Smoothly transition to new volume
                        _ = SmoothVolumeTransition(_currentVolProvider?.Volume ?? _volume, targetVolume, 500);
                    }
                }
                else if (e.PropertyName == nameof(Config.DefaultVolume))
                {
                    UpdateNormalizationStatus();
                }
                else if (e.PropertyName == nameof(Config.AutoEnqueue))
                {
                    UpdateAutopilotBadge();
                }
                else if (e.PropertyName == nameof(Config.EnableAnnouncements))
                {
                    UpdateAnnouncementButtonVisibility();
                }
            });
        }

        private async Task SmoothVolumeTransition(float fromVolume, float toVolume, int durationMs)
        {
            if (_currentVolProvider == null) return;

            int steps = 20;
            int delay = durationMs / steps;

            _isUserAdjustingVolume = true;

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float volume = fromVolume + (toVolume - fromVolume) * t;

                try 
                { 
                    _currentVolProvider.Volume = volume;

                    // Update slider to match
                    await Dispatcher.InvokeAsync(() => VolumeSlider.Value = volume);
                } 
                catch { }

                if (i < steps) await Task.Delay(delay);
            }

            _volume = toVolume;
            _isUserAdjustingVolume = false;
        }

        private void UpdateNormalizeVolumeButtonVisibility()
        {
            var button = FindName("SetDefaultVolumeButton") as Button;
            if (button != null)
            {
                button.Visibility = ConfigService.Instance.Current?.NormalizeVolume == true 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
        }

        private void UpdateAnnouncementButtonVisibility()
        {
            var button = FindName("AnnouncementButton") as Button;
            if (button == null) return;

            bool enabled = ConfigService.Instance.Current?.EnableAnnouncements ?? true;
            button.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        public async Task FadeAnnouncementDuckingAsync(double dimDb, int durationMs)
        {
            var targetFactor = (float)Math.Clamp(Math.Pow(10.0, -Math.Abs(dimDb) / 20.0), 0.01, 1.0);
            await FadeAnnouncementOutputToAsync(targetFactor, durationMs);
        }

        public async Task FadeAnnouncementRestoreAsync(int durationMs)
        {
            await FadeAnnouncementOutputToAsync(1f, durationMs);
        }

        private async Task FadeAnnouncementOutputToAsync(float targetFactor, int durationMs)
        {
            await _announcementVolumeGate.WaitAsync();
            try
            {
                float startFactor = _announcementOutputFactor;
                _announcementOutputFactor = targetFactor;

                var output = _outputDevice;
                if (output == null) return;

                int steps = Math.Max(10, durationMs / 20);
                int delay = Math.Max(10, durationMs / steps);
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float eased = 1f - (1f - t) * (1f - t);
                    float current = startFactor + (targetFactor - startFactor) * eased;
                    try
                    {
                        output.Volume = Math.Clamp(current, 0f, 1f);
                    }
                    catch
                    {
                        break;
                    }

                    if (i < steps)
                    {
                        await Task.Delay(delay);
                    }
                }

                try
                {
                    output.Volume = Math.Clamp(targetFactor, 0f, 1f);
                }
                catch
                {
                    // output device might have changed during transition.
                }
            }
            finally
            {
                _announcementVolumeGate.Release();
            }
        }

        private void UpdateAudioInfoTags(string filePath)
        {
            var fileTypeTag = FindName("FileTypeTag") as Border;
            var fileTypeText = FindName("FileTypeText") as TextBlock;
            var bitrateTag = FindName("BitrateTag") as Border;
            var bitrateText = FindName("BitrateText") as TextBlock;

            if (fileTypeTag == null || fileTypeText == null || bitrateTag == null || bitrateText == null)
                return;

            try
            {
                // Get file extension
                string extension = System.IO.Path.GetExtension(filePath).TrimStart('.').ToUpper();
                fileTypeText.Text = extension;
                fileTypeTag.Visibility = Visibility.Visible;

                // Get bitrate using TagLib
                using (var file = TagLib.File.Create(filePath))
                {
                    int bitrate = file.Properties.AudioBitrate;
                    bitrateText.Text = $"{bitrate} kbps";
                    bitrateTag.Visibility = Visibility.Visible;

                    // Reset text color to white by default
                    bitrateText.Foreground = new SolidColorBrush(Colors.White);

                    // Color-code based on quality
                    // 0-100 kbps: red
                    // 101-199 kbps: yellow
                    // 200-500 kbps: green
                    // >500 kbps: blue
                    if (bitrate <= 100)
                    {
                        bitrateTag.Background = new SolidColorBrush(Color.FromRgb(229, 57, 53)); // Red
                    }
                    else if (bitrate <= 199)
                    {
                        bitrateTag.Background = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow/Amber
                        bitrateText.Foreground = new SolidColorBrush(Colors.Black); // Better contrast on yellow
                    }
                    else if (bitrate <= 500)
                    {
                        bitrateTag.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                    }
                    else
                    {
                        bitrateTag.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue
                    }
                }
            }
            catch (Exception ex)
            {
                // If we can't read metadata, hide the tags
                System.Diagnostics.Debug.WriteLine($"Failed to read audio info: {ex.Message}");
                fileTypeTag.Visibility = Visibility.Collapsed;
                bitrateTag.Visibility = Visibility.Collapsed;
            }
        }

        private void NewPresentation()
        {
            Presentation pres = new Presentation(this);
            pres.Show();
        }

        private void ComputeQueueTimings()
        {
            // initial offset: time until the currently playing track finishes
            TimeSpan initialOffset = TimeSpan.Zero;
            if (_currentReader != null)
            {
                try { initialOffset = _currentReader.TotalTime - _currentReader.CurrentTime; } catch { initialOffset = TimeSpan.Zero; }
            }

            TimeSpan cumulative = TimeSpan.Zero;

            if (Queue.Count > 0)
            {
                Queue[0].EstimatedStartDisplay = "Playing Now";
            }

            for (int i = 1; i < Queue.Count; i++)
            {
                var song = Queue[i];
                // if duration unknown try to probe file
                if (song.Duration == TimeSpan.Zero && System.IO.File.Exists(song.songPath))
                {
                    try
                    {
                        using var afr = new AudioFileReader(song.songPath);
                        song.Duration = afr.TotalTime;
                        song.length = string.Format("{0:mm}:{0:ss}", song.Duration);
                    }
                    catch { }
                }

                // time until this song starts playing
                TimeSpan timeUntil = initialOffset + cumulative;
                song.EstimatedStart = timeUntil;
                song.EstimatedStartDisplay = "Wait Time: " + (timeUntil <= TimeSpan.FromSeconds(1) ? "Now" : timeUntil.ToString(@"mm\:ss"));

                cumulative += song.Duration;
            }

            // refresh the list UI
            QueueList.Items.Refresh();
        }

        private void PositionTimer_Tick(object sender, EventArgs e)
        {
            if (_currentReader != null && !_isUserSeeking)
            {
                var totalTime = _currentReader.TotalTime;
                if (totalTime.TotalSeconds > 0)
                {
                    var currentTime = _currentReader.CurrentTime;
                    var remainingTime = totalTime - currentTime;
                    if (remainingTime < TimeSpan.Zero) remainingTime = TimeSpan.Zero;

                    ProgressSlider.Value = Math.Clamp(currentTime.TotalSeconds / totalTime.TotalSeconds, 0.0, 1.0);
                    ElapsedText.Text = FormatMmSsRounded(currentTime);
                    RemainingText.Text = FormatMmSsRounded(remainingTime);

                    var nowUtc = DateTime.UtcNow;
                    bool runSecondaryWork = (nowUtc - _lastSecondaryPlaybackTickUtc) >= TimeSpan.FromMilliseconds(250);
                    if (!runSecondaryWork)
                    {
                        return;
                    }
                    _lastSecondaryPlaybackTickUtc = nowUtc;

                    // Automatic crossfade/start next behavior
                    if (!_isCrossfading && Queue.Count > 1)
                    {
                        double cross = CrossfadeSlider.Value;
                        if (cross <= 0) cross = 4;
                        var remaining = remainingTime.TotalSeconds;

                        // If remaining is less than or equal to crossfade seconds (start crossfade)
                        if (remaining <= cross && remaining > 0.15)
                        {
                            // start crossfade to next
                            _ = BeginCrossfadeToNext(Queue[1]);
                        }
                        else if (remaining <= 0.15)
                        {
                            // ended - fall back to immediate advance if not handled
                            _ = AdvanceToNextImmediate();
                        }
                    }
                    else if (Queue.Count == 1)
                    {
                        // Only one song left - check if it's ending
                        var remaining = remainingTime.TotalSeconds;
                        if (remaining <= 0.15)
                        {
                            // Last song ended - stop playback completely
                            StopPlayback();
                            PlayPauseButton.Content = "▶";

                            // Clear UI
                            ProgressSlider.Value = 0;
                            ElapsedText.Text = "00:00";
                            RemainingText.Text = "00:00";

                            // Clear Now Playing info
                            NowTitle.Text = "";
                            NowArtist.Text = "";
                            NowLength.Text = "";
                            NowThumbnail.Source = null;

                            // Clear lyrics
                            var lyricsPanel = FindName("LyricsStackPanel") as StackPanel;
                            if (lyricsPanel != null) lyricsPanel.Children.Clear();
                            WholeLyricsPanel.Visibility = Visibility.Hidden;
                            SetLyricsLoadingVisible(false);
                            _currentSyncedLines.Clear();
                            _currentHighlighted = null;
                            ApplyLyricsAdaptiveLayout(false, false);

                            // Notify presentation that nothing is playing
                            try
                            {
                                NowPlayingTick?.Invoke(this, new NowPlayingEventArgs
                                {
                                    Current = null,
                                    CurrentTime = TimeSpan.Zero,
                                    TotalTime = TimeSpan.Zero
                                });
                            }
                            catch { }
                        }
                    }

                    // raise tick event for external displays
                    try
                    {
                        var current = Queue.Count > 0 ? Queue[0] : null;
                        if (current != null && _currentReader != null)
                        {
                            NowPlayingTick?.Invoke(this, new NowPlayingEventArgs
                            {
                                Current = current,
                                CurrentTime = _currentReader.CurrentTime,
                                TotalTime = _currentReader.TotalTime
                            });
                        }
                    }
                    catch(Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"NowPlaying tick failed: {ex.Message}");
                    }

                    
                    if(_currentReader != null) UpdateLyricHighlighting(_currentReader.CurrentTime);
                }
            }
        }

        private static string FormatMmSsRounded(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            int seconds = (int)Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero);
            if (seconds < 0) seconds = 0;
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }

        private void UpdateLyricHighlighting(TimeSpan currentTime)
        {
            if (WholeLyricsPanel.Visibility != Visibility.Visible || LyricsLoadingPanel.Visibility == Visibility.Visible) return;
            if (_currentReader == null || LyricsStackPanel == null || LyricsScrollViewer == null) return;
            if (LyricsStackPanel.Children.Count == 0) return;

            int index = -1;
            if (_currentSyncedLines.Count > 0)
            {
                for (int i = 0; i < _currentSyncedLines.Count; i++)
                {
                    if (_currentSyncedLines[i].Time <= currentTime) index = i;
                    else break;
                }
                if (index < 0) index = 0;
            }
            else
            {
                double totalSeconds = Math.Max(1.0, _currentReader.TotalTime.TotalSeconds);
                double progress = Math.Clamp(_currentReader.CurrentTime.TotalSeconds / totalSeconds, 0.0, 1.0);
                index = (int)Math.Round(progress * Math.Max(0, LyricsStackPanel.Children.Count - 1));
            }

            if (index < 0 || index >= LyricsStackPanel.Children.Count) return;
            if (LyricsStackPanel.Children[index] is not TextBlock tb) return;

            if (_currentHighlighted != null && _currentHighlighted != tb)
            {
                var prevBrush = _currentHighlighted.Foreground as SolidColorBrush ?? new SolidColorBrush(InactiveLyricColor);
                _currentHighlighted.Foreground = prevBrush;
                prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(InactiveLyricColor, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
                _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, new DoubleAnimation(BaseLyricFontSize, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
                _currentHighlighted.BeginAnimation(OpacityProperty, new DoubleAnimation(InactiveLyricOpacity, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
            }

            var brush = tb.Foreground as SolidColorBrush ?? new SolidColorBrush(InactiveLyricColor);
            tb.Foreground = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ActiveLyricColor, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
            tb.BeginAnimation(TextBlock.FontSizeProperty, new DoubleAnimation(ActiveLyricFontSize, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(ActiveLyricOpacity, TimeSpan.FromMilliseconds(LyricAnimationDurationMs)));
            _currentHighlighted = tb;

            try
            {
                var transform = tb.TransformToAncestor(LyricsStackPanel);
                var point = transform.Transform(new Point(0, 0));
                double target = Math.Max(0, point.Y - (LyricsScrollViewer.ViewportHeight / 2) + (tb.ActualHeight / 2));
                var scrollAnim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                LyricsScrollViewer.BeginAnimation(SmoothScrollOffsetProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
            }
            catch
            {
                // Ignore transform/scroll glitches during resize transitions.
            }
        }

        private async Task BeginCrossfadeToNext(Song nextSong)
        {
            if (_isCrossfading) return;

            _isCrossfading = true;
            try
            {
                
                await CrossfadeToNext(nextSong);

                // remove the previous first entry
                if (Queue.Count > 0) Queue.RemoveAt(0);

                // ensure now is current
                if (Queue.Count > 0)
                {
                    // update UI to reflect current
                    NowTitle.Text = Queue[0].Title;
                    NowArtist.Text = Queue[0].Artist;
                    NowLength.Text = Queue[0].length;
                    if (Queue[0].thumbnail?.Source != null) NowThumbnail.Source = Queue[0].thumbnail.Source as BitmapSource;

                    TryApplyThumbnailGradient(Queue[0].thumbnail?.Source as BitmapSource);

                    // Update audio info tags for the new current song
                    UpdateAudioInfoTags(Queue[0].songPath);
                }

                if (Queue.Count > 0) _ = FetchAndDisplayLyricsFor(Queue[0]);
            }
            catch { }
            finally
            {
                _isCrossfading = false;
            }
        }

        private async Task AdvanceToNextImmediate()
        {
            // Called when the current track is basically finished - move to next without crossfade
            if (Queue.Count <= 1)
            {
                StopPlayback();
                PlayPauseButton.Content = "▶";
                return;
            }

            

            var nextSong = Queue[1];
            try
            {
                // Calculate loudness for next song if needed
                if (nextSong.PerceivedLoudness < 0)
                {
                    await Task.Run(() => CalculateSongLoudness(nextSong));
                }

                // Calculate normalized volume BEFORE starting playback
                float targetVolume = CalculateNormalizedVolume(nextSong, _volume);

                // Stop current playback
                StopPlayback();

                // Remove previous song from queue FIRST
                if (Queue.Count > 0) Queue.RemoveAt(0);

                // Now start playback - Queue[0] is the song we want to play
                var reader = new AudioFileReader(nextSong.songPath);
                StartPlaybackWithMixer(reader);

                if (Queue.Count > 0)
                {
                    NowTitle.Text = Queue[0].Title;
                    NowArtist.Text = Queue[0].Artist;
                    NowLength.Text = Queue[0].length;
                    if (Queue[0].thumbnail?.Source != null) NowThumbnail.Source = Queue[0].thumbnail.Source as BitmapSource;

                    TryApplyThumbnailGradient(Queue[0].thumbnail?.Source as BitmapSource);

                    // Update audio info tags after queue update
                    UpdateAudioInfoTags(Queue[0].songPath);
                }

                if (Queue.Count > 0) _ = FetchAndDisplayLyricsFor(Queue[0]);
            }
            catch
            {
                StopPlayback();
            }

            await Task.CompletedTask;
        }

        private void StartPlaybackWithMixer(AudioFileReader reader)
        {
            StopPlayback();

            _mixer = new MixingSampleProvider(_format) { ReadFully = true };
            _currentReader = reader;

            // Calculate normalized volume if applicable
            float targetVolume = _volume;
            if (Queue.Count > 0)
            {
                // Calculate loudness if not already done
                if (Queue[0].PerceivedLoudness < 0)
                {
                    Task.Run(() => CalculateSongLoudness(Queue[0]));
                }

                targetVolume = CalculateNormalizedVolume(Queue[0], _volume);

                // Update slider without triggering normalization disable
                _isUserAdjustingVolume = true;
                Dispatcher.Invoke(() => VolumeSlider.Value = targetVolume);
                _isUserAdjustingVolume = false;
            }

            // create a volume wrapper for the reader and keep reference
            var sp = GetSampleProviderCompatible(reader);
            _currentVolProvider = new VolumeSampleProvider(sp) { Volume = targetVolume };

            _mixer.AddMixerInput(_currentVolProvider);

            // Initialize capturing provider ONCE to wrap the mixer output
            // This captures ALL audio (current song, crossfades, etc.) at a single point
            if (_capturingProvider == null)
            {
                _capturingProvider = new CapturingSampleProvider(_mixer);
                _capturingProvider.SamplesCaptured += (sender, samples) =>
                {
                    // Forward to Music Share
                    AudioSamplesCaptured?.Invoke(this, samples);

                    // Forward to Visualization Window
                    _visualizationWindow?.UpdateAudioSamples(samples, _capturingProvider.WaveFormat.Channels);
                };
            }

            _outputDevice = new WaveOutEvent();
            if (OutputDeviceCombo.SelectedIndex >= 0)
            {
                try { _outputDevice.DeviceNumber = OutputDeviceCombo.SelectedIndex; } catch { }
            }

            // Output device reads from capturing provider (which wraps mixer)
            _outputDevice.Init(_capturingProvider);
            _outputDevice.Volume = Math.Clamp(_announcementOutputFactor, 0f, 1f);
            _outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;
            _outputDevice.Play();

            _positionTimer.Start();

            // reflect play state
            PlayPauseButton.Content = "❚❚";
            ComputeQueueTimings();

            // Update audio info tags
            if (Queue.Count > 0)
            {
                UpdateAudioInfoTags(Queue[0].songPath);
            }

            // notify listeners about immediate now-playing change
            try
            {
                var current = Queue.Count > 0 ? Queue[0] : null;
                if (current != null)
                {
                    NowPlayingTick?.Invoke(this, new NowPlayingEventArgs
                    {
                        Current = current,
                        CurrentTime = _currentReader.CurrentTime,
                        TotalTime = _currentReader.TotalTime
                    });

                    // fetch lyrics asynchronously (fire-and-forget)
                    _ = FetchAndDisplayLyricsFor(current);
                }
            }
            catch { }
        }

        private async Task FetchAndDisplayLyricsFor(Song current)
        {
            if (current == null)
            {
                LyricsStackPanel.Children.Clear();
                _currentSyncedLines.Clear();
                _currentHighlighted = null;
                SetLyricsLoadingVisible(false);
                SetLyricsPanelVisibility(false, false);
                ApplyLyricsAdaptiveLayout(false, false);
                SetLyricsProviderLabel("");
                return;
            }

            int requestVersion = ++_lyricsRequestVersion;
            string songKey = BuildLyricsSongKey(current);
            _currentLyricsSongKey = songKey;

            // Reset to normal layout immediately so lyrics-enabled songs never inherit no-lyrics sizing.
            await Dispatcher.InvokeAsync(() => ApplyLyricsAdaptiveLayout(false, false));

            var fetchTask = GetOrStartLyricsTask(current, songKey);
            _ = ShowLyricsLoadingIfSlowAsync(songKey, requestVersion, fetchTask);

            LyricsResult result;
            string providerLabel = "Lyrics: LRCLIB";
            try
            {
                result = await fetchTask.ConfigureAwait(false);
            }
            catch
            {
                result = new LyricsResult();
            }

            var fallbackEnabled = ConfigService.Instance.Current?.UseCaptionLyricsFallback ?? true;
            if (fallbackEnabled
                && (!result.Found || (!result.HasSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
                && TryGetCachedYoutubeSubtitleFallback(current.songPath, out var timedSubtitleFallback, out var plainSubtitleFallback))
            {
                result = new LyricsResult
                {
                    Found = true,
                    SyncedLyrics = timedSubtitleFallback ?? string.Empty,
                    PlainLyrics = plainSubtitleFallback ?? string.Empty
                };
                providerLabel = "Lyrics: YouTube captions";
            }
            else if (!result.Found || (!result.HasSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
            {
                providerLabel = "";
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (requestVersion != _lyricsRequestVersion || !string.Equals(songKey, _currentLyricsSongKey, StringComparison.OrdinalIgnoreCase))
                    return;

                SetLyricsLoadingVisible(false);
                SetLyricsProviderLabel(providerLabel);
                RenderLyricsResult(result);
            });

            PrefetchNextLyrics();
        }

        private void RenderLyricsResult(LyricsResult result)
        {
            LyricsStackPanel.Children.Clear();
            _currentSyncedLines.Clear();
            _currentHighlighted = null;
            LyricsScrollViewer.BeginAnimation(SmoothScrollOffsetProperty, null);
            LyricsScrollViewer.ScrollToVerticalOffset(0);

            if (result.Found && result.HasSynced)
            {
                _currentSyncedLines = result
                    .ParseSyncedLines()
                    .ToList();

                if (_currentSyncedLines.Count > 0)
                {
                    foreach (var (_, text) in _currentSyncedLines)
                    {
                        LyricsStackPanel.Children.Add(CreateLyricTextBlock(text));
                    }

                    SetLyricsPanelVisibility(true);
                    ApplyLyricsAdaptiveLayout(false);
                    return;
                }
            }

            if (result.Found)
            {
                var plainLines = (result.PlainLyrics ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (plainLines.Count > 0)
                {
                    foreach (var line in plainLines)
                    {
                        LyricsStackPanel.Children.Add(CreateLyricTextBlock(line));
                    }

                    SetLyricsPanelVisibility(true);
                    ApplyLyricsAdaptiveLayout(false);
                    return;
                }
            }

            SetLyricsPanelVisibility(false);
            ApplyLyricsAdaptiveLayout(true);
        }

        private TextBlock CreateLyricTextBlock(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = BaseLyricFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(InactiveLyricColor),
                Margin = new Thickness(0, 8, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = InactiveLyricOpacity
            };
        }

        private string BuildLyricsSongKey(Song song)
        {
            return $"{song.songPath}|{song.Artist}|{song.Title}".Trim();
        }

        private string SanitizeArtist(string artist)
        {
            return (artist ?? string.Empty).Replace(" - Topic", string.Empty).Trim();
        }

        private sealed class CachedYoutubeVideoData
        {
            public string SubtitleText { get; set; } = string.Empty;
            public string SubtitleTimedText { get; set; } = string.Empty;
        }

        private static bool TryGetCachedYoutubeSubtitleFallback(string songPath, out string timedSubtitleText, out string plainSubtitleText)
        {
            timedSubtitleText = string.Empty;
            plainSubtitleText = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(songPath)) return false;
                var videoId = Path.GetFileNameWithoutExtension(songPath);
                if (string.IsNullOrWhiteSpace(videoId)) return false;

                var cachePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, YoutubeForm.downloadPath, "video-metadata-cache.json"));
                if (!System.IO.File.Exists(cachePath)) return false;

                var json = System.IO.File.ReadAllText(cachePath);
                var cache = JsonConvert.DeserializeObject<Dictionary<string, CachedYoutubeVideoData>>(json);
                if (cache == null) return false;
                if (!cache.TryGetValue(videoId, out var cached)) return false;
                if (string.IsNullOrWhiteSpace(cached?.SubtitleText) && string.IsNullOrWhiteSpace(cached?.SubtitleTimedText)) return false;

                timedSubtitleText = cached.SubtitleTimedText;
                plainSubtitleText = cached.SubtitleText;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private Task<LyricsResult> GetOrStartLyricsTask(Song song, string songKey)
        {
            lock (_lyricsCacheLock)
            {
                if (_lyricsCache.TryGetValue(songKey, out var cached))
                {
                    return Task.FromResult(cached);
                }

                if (_lyricsInFlight.TryGetValue(songKey, out var inflight))
                {
                    return inflight;
                }

                var task = FetchLyricsInternalAsync(song, songKey);
                _lyricsInFlight[songKey] = task;
                return task;
            }
        }

        private async Task<LyricsResult> FetchLyricsInternalAsync(Song song, string songKey)
        {
            LyricsResult result = new LyricsResult();
            try
            {
                var query = LyricsQueryNormalizer.Build(song);
                result = await _lyricsService.GetCachedLyricsAsync(query.Artist, query.Title, song.Duration).ConfigureAwait(false);
                if (!result.Found)
                {
                    result = await _lyricsService.GetLyricsAsync(query.Artist, query.Title, song.Duration).ConfigureAwait(false);
                }
            }
            catch
            {
                result = new LyricsResult();
            }
            finally
            {
                lock (_lyricsCacheLock)
                {
                    _lyricsInFlight.Remove(songKey);
                    _lyricsCache[songKey] = result;
                }
            }

            return result;
        }

        private async Task ShowLyricsLoadingIfSlowAsync(string songKey, int requestVersion, Task<LyricsResult> task)
        {
            try
            {
                await Task.Delay(350).ConfigureAwait(false);
                if (task.IsCompleted) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (requestVersion != _lyricsRequestVersion || !string.Equals(songKey, _currentLyricsSongKey, StringComparison.OrdinalIgnoreCase))
                        return;

                    LyricsStackPanel.Children.Clear();
                    _currentSyncedLines.Clear();
                    _currentHighlighted = null;
                    SetLyricsPanelVisibility(true, false);
                    SetLyricsLoadingVisible(true);
                    SetLyricsProviderLabel("Lyrics: Loading...");
                });
            }
            catch
            {
                // Ignore loading indicator race conditions.
            }
        }

        private void SetLyricsLoadingVisible(bool isVisible)
        {
            LyricsLoadingPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetLyricsProviderLabel(string text)
        {
            if (LyricsProviderText != null)
            {
                LyricsProviderText.Text = text;
            }
        }

        private void SetLyricsPanelVisibility(bool isVisible, bool animate = true)
        {
            WholeLyricsPanel.BeginAnimation(OpacityProperty, null);

            if (!animate)
            {
                WholeLyricsPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Hidden;
                WholeLyricsPanel.Opacity = isVisible ? 1 : 0;
                return;
            }

            if (isVisible)
            {
                WholeLyricsPanel.Visibility = Visibility.Visible;
                var showAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                WholeLyricsPanel.BeginAnimation(OpacityProperty, showAnim, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                var hideAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                hideAnim.Completed += (_, __) =>
                {
                    WholeLyricsPanel.Visibility = Visibility.Hidden;
                    WholeLyricsPanel.Opacity = 0;
                };
                WholeLyricsPanel.BeginAnimation(OpacityProperty, hideAnim, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private void PrefetchNextLyrics()
        {
            if (Queue.Count <= 1) return;
            var next = Queue[1];
            if (next == null) return;
            string nextKey = BuildLyricsSongKey(next);
            _ = GetOrStartLyricsTask(next, nextKey);
        }

        private void ApplyLyricsAdaptiveLayout(bool noLyrics, bool animate = true)
        {
            if (NowPlayingHeader == null || NowInfoStack == null || ThumbnailBorder == null || ProgressPanel == null) return;
            _layoutTransitionVersion++;
            _isNoLyricsLayout = noLyrics;
            NowPlayingHeader.BeginAnimation(OpacityProperty, null);
            NowPlayingHeader.Opacity = 1;
            ApplyLyricsAdaptiveLayoutValues(noLyrics, animate);
        }

        private void ApplyLyricsAdaptiveLayoutValues(bool noLyrics, bool animate)
        {
            double playerWidth = Player.ActualWidth > 0 ? Player.ActualWidth : Player.Width;
            double playerHeight = Player.ActualHeight > 0 ? Player.ActualHeight : ActualHeight;
            if (double.IsNaN(playerWidth) || playerWidth <= 0) playerWidth = 835;
            if (double.IsNaN(playerHeight) || playerHeight <= 0) playerHeight = 800;

            double progressAreaHeight = ProgressRow?.ActualHeight > 0
                ? ProgressRow.ActualHeight
                : (ProgressPanel.ActualHeight > 0 ? ProgressPanel.ActualHeight + 18 : 106);
            double controlsAreaHeight = ControlsRow?.ActualHeight > 0 ? ControlsRow.ActualHeight : 130;
            double reservedBottomHeight = progressAreaHeight + controlsAreaHeight + 46;
            double availableVisualHeight = Math.Max(120, playerHeight - reservedBottomHeight);

            double targetThumbWidth;
            if (noLyrics)
            {
                double widthByContainer = Math.Max(140, (playerWidth - 52) * 0.72);
                double heightByContainer = Math.Max(130, availableVisualHeight * 0.86);
                double widthByHeight = heightByContainer * (16.0 / 9.0);
                targetThumbWidth = Math.Clamp(Math.Min(widthByContainer, widthByHeight), 140, 820);
            }
            else
            {
                targetThumbWidth = Math.Clamp((playerWidth - 88) * 0.25, 150, 280);
            }

            var targetThumbHeight = targetThumbWidth * (9.0 / 16.0);
            var targetHeaderMargin = noLyrics ? new Thickness(6, 14, 6, 6) : new Thickness(6);
            var targetInfoMargin = noLyrics
                ? new Thickness(0, Math.Clamp(targetThumbHeight * 0.07, 10, 24), 0, 0)
                : new Thickness(12, 0, 0, 0);
            var targetProgressMargin = noLyrics ? new Thickness(6, 10, 6, 8) : new Thickness(6, 6, 6, 8);

            double scale = Math.Clamp(targetThumbWidth / 448d, 0.85, 1.55);
            var targetTitleFont = noLyrics ? Math.Clamp(40d * scale, 28, 60) : 26d;
            var targetArtistFont = noLyrics ? Math.Clamp(30d * scale, 22, 44) : 24d;
            var targetLengthFont = noLyrics ? Math.Clamp(22d * scale, 18, 34) : 18d;
            bool allowTwoLineTitle = noLyrics && availableVisualHeight >= 290;
            double titleLineHeight = targetTitleFont * 1.2;

            NowPlayingHeader.Orientation = noLyrics ? Orientation.Vertical : Orientation.Horizontal;
            NowPlayingHeader.HorizontalAlignment = noLyrics ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NowPlayingHeader.VerticalAlignment = noLyrics ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            NowInfoStack.HorizontalAlignment = noLyrics ? HorizontalAlignment.Center : HorizontalAlignment.Left;

            var textAlignment = noLyrics ? TextAlignment.Center : TextAlignment.Left;
            NowTitle.TextAlignment = textAlignment;
            NowArtist.TextAlignment = textAlignment;
            NowLength.TextAlignment = textAlignment;
            NowLength.HorizontalAlignment = noLyrics ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NowTitle.TextWrapping = noLyrics ? TextWrapping.Wrap : TextWrapping.NoWrap;
            NowTitle.TextTrimming = noLyrics && allowTwoLineTitle ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            NowTitle.MaxWidth = noLyrics
                ? Math.Max(140, Math.Min(playerWidth - 60, targetThumbWidth + 120))
                : Math.Max(260, Math.Min(playerWidth - 90, 560));
            NowTitle.MaxHeight = noLyrics
                ? (allowTwoLineTitle ? titleLineHeight * 2.3 : titleLineHeight * 1.2)
                : double.PositiveInfinity;

            Grid.SetRowSpan(NowPlayingHeader, noLyrics ? 2 : 1);

            if (animate)
            {
                AnimateDouble(ThumbnailBorder, FrameworkElement.WidthProperty, targetThumbWidth, 460);
                AnimateDouble(ThumbnailBorder, FrameworkElement.HeightProperty, targetThumbHeight, 460);
                AnimateThickness(NowPlayingHeader, FrameworkElement.MarginProperty, targetHeaderMargin, 460);
                AnimateThickness(NowInfoStack, FrameworkElement.MarginProperty, targetInfoMargin, 460);
                AnimateThickness(ProgressPanel, FrameworkElement.MarginProperty, targetProgressMargin, 460);
                AnimateDouble(NowTitle, TextBlock.FontSizeProperty, targetTitleFont, 420);
                AnimateDouble(NowArtist, TextBlock.FontSizeProperty, targetArtistFont, 420);
                AnimateDouble(NowLength, TextBlock.FontSizeProperty, targetLengthFont, 420);
            }
            else
            {
                ThumbnailBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
                ThumbnailBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
                NowPlayingHeader.BeginAnimation(FrameworkElement.MarginProperty, null);
                NowInfoStack.BeginAnimation(FrameworkElement.MarginProperty, null);
                ProgressPanel.BeginAnimation(FrameworkElement.MarginProperty, null);
                NowTitle.BeginAnimation(TextBlock.FontSizeProperty, null);
                NowArtist.BeginAnimation(TextBlock.FontSizeProperty, null);
                NowLength.BeginAnimation(TextBlock.FontSizeProperty, null);

                ThumbnailBorder.Width = targetThumbWidth;
                ThumbnailBorder.Height = targetThumbHeight;
                NowPlayingHeader.Margin = targetHeaderMargin;
                NowInfoStack.Margin = targetInfoMargin;
                ProgressPanel.Margin = targetProgressMargin;
                NowTitle.FontSize = targetTitleFont;
                NowArtist.FontSize = targetArtistFont;
                NowLength.FontSize = targetLengthFont;
            }
        }

        private static void AnimateDouble(FrameworkElement target, DependencyProperty property, double value, int durationMs)
        {
            var animation = new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateThickness(FrameworkElement target, DependencyProperty property, Thickness value, int durationMs)
        {
            var animation = new ThicknessAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private async Task CrossfadeToNext(Song nextSong)
        {
            // Add nextReader to mixer and ramp volumes
            if (_currentReader == null) return;
            if (!System.IO.File.Exists(nextSong.songPath)) return;

            // Calculate loudness for next song if not done
            if (nextSong.PerceivedLoudness < 0)
            {
                await Task.Run(() => CalculateSongLoudness(nextSong));
            }

            // Calculate normalized volume for next song
            float targetNextVolume = CalculateNormalizedVolume(nextSong, _volume);

            System.Diagnostics.Debug.WriteLine($"Crossfading to {nextSong.Title}: target volume = {targetNextVolume:F2}");

            // Do not set reader.Volume - control volume via VolumeSampleProvider to avoid silent output
            _nextReader = new AudioFileReader(nextSong.songPath);

            // prepare next vol provider - start at 0 for fade in
            var nextSp = GetSampleProviderCompatible(_nextReader);
            _nextVolProvider = new VolumeSampleProvider(nextSp) { Volume = 0f };

            if (_mixer == null)
            {
                _mixer = new MixingSampleProvider(_format) { ReadFully = true };
                if (_currentVolProvider != null) _mixer.AddMixerInput(_currentVolProvider);
            }

            // Add next track to mixer (capturing provider already wraps mixer output)
            _mixer.AddMixerInput(_nextVolProvider);

            double cross = CrossfadeSlider.Value;
            if (cross <= 0) cross = 4;

            // Get current volume (might be normalized for current song)
            float currentStartVolume = _currentVolProvider?.Volume ?? _volume;

            int steps = 40;
            int delay = Math.Max(10, (int)(cross * 1000 / steps));
            for (int i = 0; i < steps; i++)
            {
                float t = (i + 1f) / steps;
                // Ramp up next to its normalized volume, ramp down current from its volume
                try { _nextVolProvider.Volume = targetNextVolume * t; } catch { }
                try { if (_currentVolProvider != null) _currentVolProvider.Volume = currentStartVolume * (1f - t); } catch { }
                await Task.Delay(delay);
            }

            // Update slider to reflect new normalized volume (without triggering manual adjustment detection)
            _isUserAdjustingVolume = true;
            await Dispatcher.InvokeAsync(() => VolumeSlider.Value = targetNextVolume);
            _isUserAdjustingVolume = false;

            _volume = targetNextVolume;

            // After fade, swap providers but DO NOT dispose current reader until it is removed from mixer
            var oldProvider = _currentVolProvider;
            var oldReader = _currentReader;

            // make the next the current
            _currentReader = _nextReader;
            _currentVolProvider = _nextVolProvider;
            _nextReader = null;
            _nextVolProvider = null;

            if (_mixer != null && oldProvider != null)
            {
                try
                {
                    _mixer.RemoveMixerInput(oldProvider);
                }
                catch
                {
                    // If removal isn't supported for some reason, fall back to recreating mixer
                    var newMixer = new MixingSampleProvider(_format) { ReadFully = true };
                    newMixer.AddMixerInput(_currentVolProvider);
                    _mixer = newMixer;
                    try { _outputDevice?.Stop(); } catch { }
                    try { _outputDevice?.Init(_mixer); } catch { }
                    try { _outputDevice?.Play(); } catch { }
                }
            }

            // Now safe to dispose the old reader
            if (oldReader != null)
            {
                try { oldReader.Dispose(); } catch { }
            }

            // ensure play button shows playing
            PlayPauseButton.Content = "❚❚";
        }

        private void StopPlayback()
        {
            _positionTimer.Stop();
            if (_outputDevice != null)
            {
                try { _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped; } catch { }
                try { _outputDevice.Stop(); } catch { }
                try { _outputDevice.Dispose(); } catch { }
                _outputDevice = null;
            }
            if (_currentReader != null)
            {
                try { _currentReader.Dispose(); } catch { }
                _currentReader = null;
            }
            if (_nextReader != null)
            {
                try { _nextReader.Dispose(); } catch { }
                _nextReader = null;
            }

            _currentVolProvider = null;
            _nextVolProvider = null;
            _mixer = null;
            _capturingProvider = null;

            // Hide audio info tags
            var fileTypeTag = FindName("FileTypeTag") as Border;
            var bitrateTag = FindName("BitrateTag") as Border;
            if (fileTypeTag != null) fileTypeTag.Visibility = Visibility.Collapsed;
            if (bitrateTag != null) bitrateTag.Visibility = Visibility.Collapsed;
        }

        private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // If playback stopped unexpectedly and there is more in the queue, try to advance
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (!_isCrossfading && Queue.Count > 1)
                {
                    _ = AdvanceToNextImmediate();
                }
                else
                {
                    // reflect stopped UI
                    PlayPauseButton.Content = "▶";
                }
            }));
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_outputDevice == null)
            {
                if (Queue.Count > 0)
                {
                    PlaySong(Queue[0]);
                }
                return;
            }

            if (_outputDevice.PlaybackState == PlaybackState.Playing)
            {
                _outputDevice.Pause();
                PlayPauseButton.Content = "▶";
            }
            else
            {
                _outputDevice.Play();
                PlayPauseButton.Content = "❚❚";
            }
        }

        private async void PlaySong(Song s)
        {
            if (s == null) return;
            if (!System.IO.File.Exists(s.songPath)) return;

            try
            {
                var reader = new AudioFileReader(s.songPath);
                StartPlaybackWithMixer(reader);

                NowTitle.Text = s.Title;
                NowArtist.Text = s.Artist;
                NowLength.Text = s.length;
                if (s.thumbnail?.Source != null) NowThumbnail.Source = s.thumbnail.Source as BitmapSource;

                // animate thumbnail pop
                AnimateThumbnailPop();

                TryApplyThumbnailGradient(s.thumbnail?.Source as BitmapSource);

                ComputeQueueTimings();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Playback error: " + ex.Message);
                StopPlayback();
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (Queue.Count <= 1)
            {
                if (Queue.Count == 1) Queue.RemoveAt(0);
                StopPlayback();
                return;
            }

            var nextSong = Queue[1];
            await BeginCrossfadeToNext(nextSong);
            ComputeQueueTimings();
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentReader != null) _currentReader.CurrentTime = TimeSpan.Zero;
            ComputeQueueTimings();
        }

        private bool TryCreateSongFromFile(string filePath, out Song? song, out string errorMessage)
        {
            song = null;
            errorMessage = string.Empty;

            try
            {
                using var afr = new AudioFileReader(filePath);
                var duration = afr.TotalTime;

                string title = System.IO.Path.GetFileNameWithoutExtension(filePath);
                string artist = "Unknown";
                var img = new Image();
                img.Source = new BitmapImage(new Uri("pack://application:,,,/SRshortLogo.png"));

                try
                {
                    var tfile = TagLib.File.Create(filePath);
                    if (!string.IsNullOrWhiteSpace(tfile.Tag.Title)) title = tfile.Tag.Title;
                    if (tfile.Tag.Performers != null && tfile.Tag.Performers.Length > 0)
                    {
                        artist = string.Join(", ", tfile.Tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                        if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown";
                    }

                    var pic = tfile.Tag.Pictures?.FirstOrDefault();
                    if (pic != null && pic.Data != null && pic.Data.Data != null && pic.Data.Data.Length > 0)
                    {
                        using var ms = new MemoryStream(pic.Data.Data);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        img.Source = bmp;
                    }
                }
                catch
                {
                    // ignore metadata failures and use defaults
                }

                song = new Song(title, artist, img, duration, filePath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void AddSongToQueue(Song song, int? insertIndex = null)
        {
            if (insertIndex.HasValue && insertIndex.Value <= Queue.Count)
            {
                Queue.Insert(insertIndex.Value, song);
            }
            else
            {
                Queue.Add(song);
            }

            AnimateListItem(song);

            var cfg = ConfigService.Instance.Current;
            if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
            {
                _ = Task.Run(() => CalculateSongLoudness(song));
            }
        }

        private static string? ResolvePlaylistEntryPath(string entry, string playlistDirectory)
        {
            if (string.IsNullOrWhiteSpace(entry)) return null;

            if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return uri.LocalPath;
            }

            if (System.IO.Path.IsPathRooted(entry))
            {
                return entry;
            }

            var baseDirectory = string.IsNullOrWhiteSpace(playlistDirectory) ? Environment.CurrentDirectory : playlistDirectory;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, entry));
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog() { Filter = "Audio files|*.mp3;*.wav;*.m4a;*.flac|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                foreach (var file in dlg.FileNames)
                {
                    if (TryCreateSongFromFile(file, out var song, out var errorMessage) && song != null)
                    {
                        AddSongToQueue(song);
                    }
                    else
                    {
                        MessageBox.Show("Unable to load file: " + errorMessage);
                    }
                }

                ComputeQueueTimings();
            }
        }

        private void AddNextButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog() { Filter = "Audio files|*.mp3;*.wav;*.m4a;*.flac|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                // decide base insertion index
                int insertIndex = Queue.Count > 1 ? 1 : Queue.Count;

                foreach (var file in dlg.FileNames)
                {
                    if (TryCreateSongFromFile(file, out var song, out var errorMessage) && song != null)
                    {
                        AddSongToQueue(song, insertIndex);
                        if (insertIndex <= Queue.Count) insertIndex++;
                    }
                    else
                    {
                        MessageBox.Show("Unable to load file: " + errorMessage);
                    }
                }

                ComputeQueueTimings();
            }
        }

        private void ImportPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog()
            {
                Filter = "M3U8 playlist|*.m3u8|M3U playlist|*.m3u|All files|*.*",
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var playlistDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
                var entries = System.IO.File.ReadLines(dlg.FileName)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                    .ToList();

                int importedCount = 0;
                int skippedCount = 0;

                foreach (var entry in entries)
                {
                    string? resolvedPath;
                    try
                    {
                        resolvedPath = ResolvePlaylistEntryPath(entry, playlistDirectory);
                    }
                    catch
                    {
                        skippedCount++;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(resolvedPath) || !System.IO.File.Exists(resolvedPath))
                    {
                        skippedCount++;
                        continue;
                    }

                    if (TryCreateSongFromFile(resolvedPath, out var song, out _) && song != null)
                    {
                        AddSongToQueue(song);
                        importedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }

                if (importedCount > 0)
                {
                    ComputeQueueTimings();
                }

                MessageBox.Show(
                    $"Imported {importedCount} song(s). Skipped {skippedCount} entr{(skippedCount == 1 ? "y" : "ies")}.",
                    "Import Playlist");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to import playlist: " + ex.Message);
            }
        }

        private void ExportPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var exportableSongs = Queue.Where(song => !string.IsNullOrWhiteSpace(song.songPath)).ToList();
            if (exportableSongs.Count == 0)
            {
                MessageBox.Show("The queue does not contain any exportable songs.", "Export Playlist");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog()
            {
                Filter = "M3U8 playlist|*.m3u8|M3U playlist|*.m3u|All files|*.*",
                DefaultExt = ".m3u8",
                AddExtension = true,
                FileName = "playlist.m3u8"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var lines = new List<string> { "#EXTM3U" };
                foreach (var song in exportableSongs)
                {
                    var durationSeconds = Math.Max(0, (int)Math.Round(song.Duration.TotalSeconds));
                    var title = string.IsNullOrWhiteSpace(song.Title) ? System.IO.Path.GetFileNameWithoutExtension(song.songPath) : song.Title;
                    var artist = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown" : song.Artist;
                    lines.Add($"#EXTINF:{durationSeconds},{artist} - {title}");
                    lines.Add(song.songPath);
                }

                System.IO.File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(false));
                MessageBox.Show($"Exported {exportableSongs.Count} song(s).", "Export Playlist");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to export playlist: " + ex.Message);
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (QueueList.SelectedItem is Song s) Queue.Remove(s);
            ComputeQueueTimings();
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (QueueList.SelectedItem is Song s)
            {
                int idx = Queue.IndexOf(s);
                if (idx > 0) { Queue.RemoveAt(idx); Queue.Insert(idx - 1, s); QueueList.SelectedItem = s; }
                ComputeQueueTimings();
            }
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (QueueList.SelectedItem is Song s)
            {
                int idx = Queue.IndexOf(s);
                if (idx < Queue.Count - 1) { Queue.RemoveAt(idx); Queue.Insert(idx + 1, s); QueueList.SelectedItem = s; }
                ComputeQueueTimings();
            }
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            var rnd = new Random();
            var items = Queue.ToList();
            Queue.Clear();
            foreach (var i in items.OrderBy(x => rnd.Next())) Queue.Add(i);
            ComputeQueueTimings();
        }

        private void AnimateListItem(Song song)
        {
            // Attempt to get the container; it may not be generated yet
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = QueueList.ItemContainerGenerator.ContainerFromItem(song) as ListBoxItem;
                if (container == null) return;

                // setup initial state
                container.Opacity = 0;
                var tt = new TranslateTransform(0, 8);
                container.RenderTransform = tt;

                var da = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var ta = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };

                container.BeginAnimation(UIElement.OpacityProperty, da);
                tt.BeginAnimation(TranslateTransform.YProperty, ta);
            }));
        }

        private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentReader != null)
            {
                double pos = ProgressSlider.Value * _currentReader.TotalTime.TotalSeconds;
                _currentReader.CurrentTime = TimeSpan.FromSeconds(pos);
                ComputeQueueTimings();
            }
            _isUserSeeking = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _stateTimer?.Stop();
            DeletePlayerState();
            base.OnClosed(e);
            StopPlayback();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            // Stop current playback but do not advance the queue
            StopPlayback();

            // Reset UI playback indicators
            ProgressSlider.Value = 0;
            ElapsedText.Text = "00:00";
            RemainingText.Text = "00:00";
            PlayPauseButton.Content = "▶";
        }

        // Ensure sample provider matches mixer format: channels and sample rate
        private ISampleProvider GetMixerInput(AudioFileReader reader)
        {
            ISampleProvider sp = reader.ToSampleProvider();

            // Ensure stereo
            if (sp.WaveFormat.Channels == 1 && _format.Channels == 2)
            {
                sp = new MonoToStereoSampleProvider(sp);
            }

            // Resample if needed
            if (sp.WaveFormat.SampleRate != _format.SampleRate)
            {
                sp = new WdlResamplingSampleProvider(sp, _format.SampleRate);
            }

            return sp;
        }

        // New helper to get compatible provider without volume wrapper
        private ISampleProvider GetSampleProviderCompatible(AudioFileReader reader)
        {
            var sp = GetMixerInput(reader);
            return sp;
        }

        // Extract two dark colors from a thumbnail and apply as background gradient for the Player Border
        private void TryApplyThumbnailGradient(BitmapSource? bmp)
        {
            if (bmp == null) return;
            try
            {
                var (c1, c2) = ExtractTwoDarkColors(bmp);
                var lg = new LinearGradientBrush(c1, c2, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                Player.Background = lg;
            }
            catch { }
        }

        private (Color, Color) ExtractTwoDarkColors(BitmapSource bmp)
        {
            // Convert to Bgra32 for easy pixel access
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[height * stride];
            formatted.CopyPixels(pixels, stride, 0);

            // simple quantization map to reduce unique keys
            var counts = new Dictionary<int, int>();
            for (int y = 0; y < height; y += Math.Max(1, height / 60)) // sample up to ~60 lines
            {
                for (int x = 0; x < width; x += Math.Max(1, width / 60))
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx + 0];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    // compute luminance
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (lum > 140) continue; // skip bright pixels (we want dark ones)

                    // quantize to 5 bits per channel
                    int rq = (r >> 3) & 0x1F;
                    int gq = (g >> 3) & 0x1F;
                    int bq = (b >> 3) & 0x1F;
                    int qKey = (rq << 10) | (gq << 5) | bq;
                    counts.TryGetValue(qKey, out int cv);
                    counts[qKey] = cv + 1;
                }
            }

            if (counts.Count == 0)
            {
                // fallback to near-black gradient
                return (Color.FromRgb(20, 20, 20), Color.FromRgb(45, 45, 45));
            }

            var top = counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToArray();
            // pick two most frequent distinct
            int k1 = top[0];
            int k2 = top.Length > 1 ? top[1] : top[0];

            Color ToColor(int key)
            {
                int rq = (key >> 10) & 0x1F;
                int gq = (key >> 5) & 0x1F;
                int bq = key & 0x1F;
                byte r = (byte)((rq << 3) | (rq >> 2));
                byte g = (byte)((gq << 3) | (gq >> 2));
                byte b = (byte)((bq << 3) | (bq >> 2));
                return Color.FromRgb(r, g, b);
            }

            var c1 = ToColor(k1);
            var c2 = ToColor(k2 == k1 ? top.FirstOrDefault() : k2);
            return (c1, c2);
        }

        // Public helper to clear queue; optionally stop playback
        public void ClearQueue(bool stopPlayback = false)
        {
            if (stopPlayback) StopPlayback();
            Queue.Clear();
            ComputeQueueTimings();
        }

        public bool RemoteIsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
        public bool RemoteCanControlVolume => ConfigService.Instance.Current?.NormalizeVolume != true;
        public double RemoteVolumeValue => Math.Clamp(VolumeSlider.Value, 0.0, 1.0);
        public bool RemoteAnnouncementIsActive => _announcementWindow?.IsLoaded == true && _announcementWindow.IsAnnouncementActive;
        public double RemoteAnnouncementDimNormalized
        {
            get
            {
                double dimDb = ConfigService.Instance.Current?.AnnouncementDimDb ?? 20.0;
                return Math.Clamp(dimDb / 50.0, 0.0, 1.0);
            }
        }
        public bool RemoteAnnouncementPushToTalkEnabled => ConfigService.Instance.Current?.AnnouncementPushToTalk ?? true;
        public double RemoteCrossfadeNormalized
        {
            get
            {
                double range = CrossfadeSlider.Maximum - CrossfadeSlider.Minimum;
                if (range <= 0) return 0;
                return Math.Clamp((CrossfadeSlider.Value - CrossfadeSlider.Minimum) / range, 0.0, 1.0);
            }
        }

        public void RemoteTogglePlayPause()
        {
            PlayPauseButton_Click(PlayPauseButton, new RoutedEventArgs(Button.ClickEvent));
        }

        public void RemoteSkipNext()
        {
            NextButton_Click(NextButton, new RoutedEventArgs(Button.ClickEvent));
        }

        public void RemotePrevious()
        {
            PrevButton_Click(PrevButton, new RoutedEventArgs(Button.ClickEvent));
        }

        public void RemoteStop()
        {
            StopButton_Click(StopButton, new RoutedEventArgs(Button.ClickEvent));
        }

        public void RemoteAdjustVolume(double delta)
        {
            if (!RemoteCanControlVolume) return;
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
        }

        public void RemoteSetVolumeFromMidi(double normalizedValue)
        {
            if (!RemoteCanControlVolume) return;
            double target = Math.Clamp(normalizedValue, 0.0, 1.0);
            VolumeSlider.Value = VolumeSlider.Minimum + (VolumeSlider.Maximum - VolumeSlider.Minimum) * target;
        }

        public void RemoteAdjustCrossfade(double delta)
        {
            CrossfadeSlider.Value = Math.Clamp(CrossfadeSlider.Value + delta, CrossfadeSlider.Minimum, CrossfadeSlider.Maximum);
        }

        public void RemoteSetCrossfadeFromMidi(double normalizedValue)
        {
            double target = Math.Clamp(normalizedValue, 0.0, 1.0);
            CrossfadeSlider.Value = CrossfadeSlider.Minimum + (CrossfadeSlider.Maximum - CrossfadeSlider.Minimum) * target;
        }

        public async Task RemoteAnnouncementActionAsync()
        {
            var window = EnsureAnnouncementWindowForRemote();
            await window.RemotePressOrToggleAsync();
        }

        public async Task RemoteAnnouncementReleaseAsync()
        {
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                await _announcementWindow.RemoteReleaseAsync();
            }
        }

        public void RemoteToggleAnnouncementPlaySound()
        {
            bool enabled = !(ConfigService.Instance.Current?.AnnouncementPlaySound ?? true);
            ConfigService.Instance.Update(cfg => cfg.AnnouncementPlaySound = enabled);
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                _announcementWindow.RemoteSetPlaySoundSetting(enabled);
            }
        }

        public void RemoteToggleAnnouncementPushToTalk()
        {
            bool enabled = !(ConfigService.Instance.Current?.AnnouncementPushToTalk ?? true);
            ConfigService.Instance.Update(cfg => cfg.AnnouncementPushToTalk = enabled);
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                _announcementWindow.RemoteSetPushToTalkSetting(enabled);
            }
        }

        public void RemoteAdjustAnnouncementDimDb(double delta)
        {
            double current = ConfigService.Instance.Current?.AnnouncementDimDb ?? 20.0;
            double target = Math.Clamp(current + delta, 0.0, 50.0);
            ConfigService.Instance.Update(cfg => cfg.AnnouncementDimDb = target);
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                _announcementWindow.RemoteSetDimDb(target);
            }
        }

        public void RemoteSetAnnouncementDimDbFromMidi(double normalizedValue)
        {
            double target = Math.Clamp(normalizedValue, 0.0, 1.0) * 50.0;
            ConfigService.Instance.Update(cfg => cfg.AnnouncementDimDb = target);
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                _announcementWindow.RemoteSetDimDb(target);
            }
        }

        private void ClearButton_OnClickButton_Click(object sender, RoutedEventArgs e)
        {
            ClearQueue();
        }

        private void NewPresButton_OnClick(object sender, RoutedEventArgs e)
        {
            NewPresentation();
        }

        private void MusicShareButton_Click(object sender, RoutedEventArgs e)
        {
            OpenMusicShareWindow();
        }

        private void AnnouncementButton_Click(object sender, RoutedEventArgs e)
        {
            OpenAnnouncementWindow();
        }

        public void OpenMusicShareWindow()
        {
            // Only allow one MusicShare window at a time
            if (_musicShareWindow != null && _musicShareWindow.IsLoaded)
            {
                // Window already exists - bring it to front
                _musicShareWindow.Activate();
                _musicShareWindow.Focus();
                return;
            }

            // Create new MusicShare window
            _musicShareWindow = new MusicShare
            {
                Owner = this
            };

            // Clean up reference when window closes
            _musicShareWindow.Closed += (s, args) => { _musicShareWindow = null; };

            _musicShareWindow.Show();
        }

        public void OpenAnnouncementWindow()
        {
            if (_announcementWindow != null && _announcementWindow.IsLoaded)
            {
                _announcementWindow.Activate();
                _announcementWindow.Focus();
                return;
            }

            _announcementWindow = new AnnouncementWindow(this)
            {
                Owner = this
            };

            _announcementWindow.Closed += (s, args) => { _announcementWindow = null; };
            _announcementWindow.Show();
        }

        private AnnouncementWindow EnsureAnnouncementWindowForRemote()
        {
            OpenAnnouncementWindow();
            return _announcementWindow!;
        }

        private void VisualizationButton_Click(object sender, RoutedEventArgs e)
        {
            // Only allow one Visualization window at a time
            if (_visualizationWindow != null && !_visualizationWindow.IsClosed())
            {
                // Window already exists - bring it to front
                _visualizationWindow.Activate();
                _visualizationWindow.Focus();
                return;
            }

            // Create new Visualization window
            _visualizationWindow = new VisualizationWindow
            {
                Owner = this
            };

            // Clean up reference when window closes
            _visualizationWindow.Closed += (s, args) => { _visualizationWindow = null; };

            _visualizationWindow.Show();
        }

        private void AnimateThumbnailPop()
        {
            try
            {
                var scale = new ScaleTransform(1, 1);
                ThumbnailBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                ThumbnailBorder.RenderTransform = scale;
                var anim = new DoubleAnimation(1.0, 1.06, TimeSpan.FromMilliseconds(160)) { AutoReverse = true };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            catch { }
        }

        public void AddSongExternal(Song s)
        {
            // the external song does NOT provide duration, so we need to probe the file
            if (s == null)
            {
                return;
            }

            try
            {
                using var afr = new AudioFileReader(s.songPath);
                s.Duration = afr.TotalTime;
                s.length = string.Format("{0:mm}:{0:ss}", s.Duration);
                Queue.Add(s);
                ComputeQueueTimings();
                AnimateListItem(s);

                // Calculate loudness in background if normalization is enabled
                var cfg = ConfigService.Instance.Current;
                if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
                {
                    _ = Task.Run(() => CalculateSongLoudness(s));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to load file: " + ex.Message);
            }
        }

        public void AddSong(Song s)
        {
            if (s == null) return;

            try
            {
                Queue.Add(s);
                ComputeQueueTimings();
                AnimateListItem(s);

                // Calculate loudness in background if normalization is enabled
                var cfg = ConfigService.Instance.Current;
                if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
                {
                    _ = Task.Run(() => CalculateSongLoudness(s));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to add song to queue: " + ex.Message);
            }
        }

        public void AddNextSongExternal(Song s)
        {
            // the external song does NOT provide duration, so we need to probe the file
            if (s == null)
            {
                return;
            }

            try
            {
                using var afr = new AudioFileReader(s.songPath);
                s.Duration = afr.TotalTime;
                s.length = string.Format("{0:mm}:{0:ss}", s.Duration);
                if (Queue.Count > 1) { Queue.Insert(1, s); AnimateListItem(s); } else { Queue.Add(s); AnimateListItem(s); }
                ComputeQueueTimings();

                // Calculate loudness in background if normalization is enabled
                var cfg = ConfigService.Instance.Current;
                if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
                {
                    _ = Task.Run(() => CalculateSongLoudness(s));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to load file: " + ex.Message);
            }
        }

        private void SetDefaultVolumeButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null || !cfg.NormalizeVolume) 
            {
                MessageBox.Show("Volume normalization is not enabled.\nPlease enable it in Settings first.", 
                    "Normalization Disabled", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
                return;
            }

            // Save current volume as default
            float currentVolume = (float)VolumeSlider.Value;
            cfg.DefaultVolume = currentVolume;
            _targetLoudness = -14.0; // Standard loudness target (LUFS)

            // Activate normalization (this will trigger Config_PropertyChanged which handles volume adjustment)
            cfg.NormalizationActive = true;

            // Update status display
            UpdateNormalizationStatus();

            // Calculate loudness for all songs in queue
            _ = CalculateLoudnessForQueueAsync();

            // If currently playing, recalculate and apply normalized volume for current song
            if (_currentReader != null && Queue.Count > 0)
            {
                if (Queue[0].PerceivedLoudness < 0)
                {
                    _ = Task.Run(() => CalculateSongLoudness(Queue[0]));
                }
            }

            MessageBox.Show($"Default volume set to {(currentVolume * 100):F0}%\nNormalization is now active.\n\nSongs will be automatically adjusted to maintain consistent loudness.", 
                "Volume Normalization Active", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _volume = (float)VolumeSlider.Value;

            // Update current playback volume
            if (_currentVolProvider != null) 
                _currentVolProvider.Volume = _volume;

            var cfg = ConfigService.Instance.Current;

            // If normalization is active and user manually changes volume, disable it
            if (cfg != null && cfg.NormalizationActive && !_isUserAdjustingVolume)
            {
                // User is manually adjusting - disable normalization
                cfg.NormalizationActive = false;
                UpdateNormalizationStatus();

                System.Diagnostics.Debug.WriteLine("User manually adjusted volume - normalization disabled");
            }
        }

        private void UpdateNormalizationStatus()
        {
            var normTag = FindName("NormalizationTag") as Border;
            var normText = FindName("NormalizationText") as TextBlock;
            var cfg = ConfigService.Instance.Current;

            if (normTag == null || normText == null || cfg == null) return;

            if (!cfg.NormalizeVolume || cfg.DefaultVolume < 0)
            {
                // Normalization not configured
                normTag.Visibility = Visibility.Collapsed;
            }
            else if (cfg.NormalizationActive)
            {
                // Active - green
                normTag.Visibility = Visibility.Visible;
                normTag.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                normText.Text = "NORM ON";
                normText.Foreground = new SolidColorBrush(Colors.White);
            }
            else if (cfg.DefaultVolume >= 0)
            {
                // Configured but inactive (user changed volume) - yellow
                normTag.Visibility = Visibility.Visible;
                normTag.Background = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow/Amber
                normText.Text = "NORM OFF";
                normText.Foreground = new SolidColorBrush(Colors.Black);
            }
        }

        private void UpdateAutopilotBadge()
        {
            var badge = FindName("AutopilotBadgeOuter") as Border;
            if (badge == null) return;

            bool enabled = ConfigService.Instance.Current?.AutoEnqueue == true;
            badge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task CalculateLoudnessForQueueAsync()
        {
            foreach (var song in Queue)
            {
                if (song.PerceivedLoudness < 0) // Not calculated yet
                {
                    await Task.Run(() => CalculateSongLoudness(song));
                }
            }
        }

        private void CalculateSongLoudness(Song song)
        {
            try
            {
                if (!System.IO.File.Exists(song.songPath))
                    return;

                using (var reader = new AudioFileReader(song.songPath))
                {
                    // Calculate RMS (Root Mean Square) loudness
                    // This is a simplified loudness calculation
                    // For production, consider using a proper LUFS library

                    float[] buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
                    double sumSquares = 0;
                    long totalSamples = 0;
                    int samplesRead;

                    // Sample throughout the file (every 10 seconds to avoid reading entire file)
                    int sampleInterval = reader.WaveFormat.SampleRate * reader.WaveFormat.Channels * 10;

                    while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int i = 0; i < samplesRead; i++)
                        {
                            sumSquares += buffer[i] * buffer[i];
                        }
                        totalSamples += samplesRead;

                        // Skip ahead to next sample point
                        if (reader.Position + sampleInterval < reader.Length)
                        {
                            reader.Position += sampleInterval;
                        }
                    }

                    if (totalSamples > 0)
                    {
                        double rms = Math.Sqrt(sumSquares / totalSamples);
                        // Convert RMS to approximate LUFS (-23 LUFS is very quiet, -14 LUFS is standard streaming)
                        double lufs = 20 * Math.Log10(rms + 0.0000001) - 0.691; // -0.691 is a calibration factor
                        song.PerceivedLoudness = lufs;

                        System.Diagnostics.Debug.WriteLine($"Calculated loudness for {song.Title}: {lufs:F1} LUFS");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to calculate loudness for {song.Title}: {ex.Message}");
                song.PerceivedLoudness = _targetLoudness; // Use target as fallback
            }
        }

        private float CalculateNormalizedVolume(Song song, float baseVolume)
        {
            var cfg = ConfigService.Instance.Current;

            if (cfg == null || !cfg.NormalizeVolume || !cfg.NormalizationActive || 
                cfg.DefaultVolume < 0 || song.PerceivedLoudness < -50)
            {
                return baseVolume;
            }

            // Calculate volume adjustment needed
            double loudnessDiff = _targetLoudness - song.PerceivedLoudness;

            // Convert to linear gain (dB to linear)
            float gainAdjustment = (float)Math.Pow(10, loudnessDiff / 20.0);

            // Apply to default volume
            float normalizedVolume = cfg.DefaultVolume * gainAdjustment;

            // Clamp to valid range
            normalizedVolume = Math.Clamp(normalizedVolume, 0.0f, 1.0f);

            System.Diagnostics.Debug.WriteLine($"Normalized volume for {song.Title}: {normalizedVolume:F2} (base: {baseVolume:F2}, loudness: {song.PerceivedLoudness:F1} LUFS)");

            return normalizedVolume;
        }

        // ── Crash-recovery helpers ────────────────────────────────────────────────

        private async void MusicPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckAndRestorePlayerState();
        }

        private async Task CheckAndRestorePlayerState()
        {
            var path = GetPlayerStatePath();
            if (!System.IO.File.Exists(path)) return;

            var result = MessageBox.Show(
                "A previous music player session was found.\n" +
                "This may have been caused by an unexpected shutdown or crash.\n\n" +
                "Would you like to restore the queue and playback position?",
                "Restore Previous Session",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                DeletePlayerState();
                return;
            }

            try
            {
                var json = System.IO.File.ReadAllText(path);
                var snapshot = JsonConvert.DeserializeObject<PlayerSnapshot>(json);

                if (snapshot?.Songs == null || snapshot.Songs.Count == 0)
                {
                    DeletePlayerState();
                    return;
                }

                foreach (var ss in snapshot.Songs)
                {
                    if (!System.IO.File.Exists(ss.SongPath)) continue;

                    try
                    {
                        using var afr = new AudioFileReader(ss.SongPath);
                        var duration = afr.TotalTime;

                        var img = new Image();
                        img.Source = new BitmapImage(new Uri("pack://application:,,,/SRshortLogo.png"));

                        try
                        {
                            var tfile = TagLib.File.Create(ss.SongPath);
                            var pic = tfile.Tag.Pictures?.FirstOrDefault();
                            if (pic?.Data?.Data != null && pic.Data.Data.Length > 0)
                            {
                                using var ms = new MemoryStream(pic.Data.Data);
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.StreamSource = ms;
                                bmp.EndInit();
                                bmp.Freeze();
                                img.Source = bmp;
                            }
                        }
                        catch { }

                        var song = new Song(ss.Title, ss.Artist, img, duration, ss.SongPath)
                        {
                            PerceivedLoudness = ss.PerceivedLoudness
                        };
                        Queue.Add(song);
                    }
                    catch { }
                }

                if (snapshot.Volume >= 0 && snapshot.Volume <= 1)
                {
                    _volume = snapshot.Volume;
                    VolumeSlider.Value = _volume;
                }

                if (Queue.Count > 0)
                {
                    PlaySong(Queue[0]);

                    double restorePos = snapshot.PlaybackPositionSeconds;
                    if (restorePos > 0 && _currentReader != null &&
                        restorePos < _currentReader.TotalTime.TotalSeconds)
                    {
                        _currentReader.CurrentTime = TimeSpan.FromSeconds(restorePos);
                    }
                }

                DeletePlayerState();
                ComputeQueueTimings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to restore player state: {ex.Message}");
                DeletePlayerState();
            }
        }

        private void SavePlayerState()
        {
            try
            {
                if (Queue.Count == 0) return;

                var snapshot = new PlayerSnapshot
                {
                    Songs = Queue.Select(s => new SongSnapshot
                    {
                        Title = s.Title,
                        Artist = s.Artist,
                        SongPath = s.songPath,
                        DurationSeconds = s.Duration.TotalSeconds,
                        PerceivedLoudness = s.PerceivedLoudness
                    }).ToList(),
                    PlaybackPositionSeconds = _currentReader?.CurrentTime.TotalSeconds ?? 0,
                    Volume = _volume,
                    SavedAt = DateTime.UtcNow
                };

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                System.IO.File.WriteAllText(GetPlayerStatePath(), json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save player state: {ex.Message}");
            }
        }

        private void DeletePlayerState()
        {
            try
            {
                var path = GetPlayerStatePath();
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete player state: {ex.Message}");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
