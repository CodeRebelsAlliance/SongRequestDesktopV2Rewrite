using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private MusicShare? _musicShareWindow = null;

        // Visualization window
        private VisualizationWindow? _visualizationWindow = null;

        public MusicPlayer()
        {
            InitializeComponent();

            QueueList.ItemsSource = Queue;

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
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
            Queue.CollectionChanged += (s, e) => { ComputeQueueTimings(); QueueChanged?.Invoke(this, EventArgs.Empty); };

            // Setup normalize volume button visibility
            UpdateNormalizeVolumeButtonVisibility();
            UpdateNormalizationStatus();

            ConfigService.Instance.Current.PropertyChanged += Config_PropertyChanged;
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
                if (_currentReader.TotalTime.TotalSeconds > 0)
                {
                    ProgressSlider.Value = _currentReader.CurrentTime.TotalSeconds / _currentReader.TotalTime.TotalSeconds;
                    ElapsedText.Text = _currentReader.CurrentTime.ToString(@"mm\:ss");
                    RemainingText.Text = (_currentReader.TotalTime - _currentReader.CurrentTime).ToString(@"mm\:ss");

                    // Automatic crossfade/start next behavior
                    if (!_isCrossfading && Queue.Count > 1)
                    {
                        double cross = CrossfadeSlider.Value;
                        if (cross <= 0) cross = 4;
                        var remaining = (_currentReader.TotalTime - _currentReader.CurrentTime).TotalSeconds;

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
                        var remaining = (_currentReader.TotalTime - _currentReader.CurrentTime).TotalSeconds;
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
                            _currentSyncedLines.Clear();
                            _currentHighlighted = null;

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
                        MessageBox.Show("Error " + ex.Message);
                    }

                    
                    if(_currentReader != null) UpdateLyricHighlighting(_currentReader.CurrentTime);
                }
            }
        }

        private void UpdateLyricHighlighting(TimeSpan currentTime)
        {
            var lyricsPanel = FindName("LyricsStackPanel") as StackPanel;
            var lyricsScroll = FindName("LyricsScrollViewer") as ScrollViewer;
            if (lyricsPanel == null || lyricsScroll == null || _currentReader == null) return;            

            int index = -1;
            if (_currentSyncedLines != null && _currentSyncedLines.Count > 0)
            {
                // find last line where Time <= currentTime
                for (int i = 0; i < _currentSyncedLines.Count; i++)
                {
                    if (_currentSyncedLines[i].Time <= currentTime) index = i; else break;
                }
                if (index < 0) index = 0;
            }
            else
            {
                // No synced lyrics: approximate current line from playback progress
                int count = lyricsPanel.Children.Count;
                if (count == 0) return;
                if (_currentReader == null || _currentReader.TotalTime.TotalSeconds <= 0)
                {
                    index = 0;
                }
                else
                {
                    double prog = Math.Clamp(_currentReader.CurrentTime.TotalSeconds / Math.Max(1.0, _currentReader.TotalTime.TotalSeconds), 0.0, 1.0);
                    index = (int)Math.Round(prog * (count - 1));
                }
            }

            if (index < 0 || index >= lyricsPanel.Children.Count) return;

            var tb = lyricsPanel.Children[index] as TextBlock;
            if (tb == null) return;

            // Revert previous highlighted line
            if (_currentHighlighted != null && _currentHighlighted != tb)
            {
                try
                {
                    var prevBrush = _currentHighlighted.Foreground as SolidColorBrush;
                    if (prevBrush == null)
                    {
                        prevBrush = new SolidColorBrush(Colors.LightGray);
                        _currentHighlighted.Foreground = prevBrush;
                    }

                    var colorAnimPrev = new ColorAnimation(Colors.LightGray, TimeSpan.FromMilliseconds(300));
                    colorAnimPrev.Completed += (s, e) =>
                    {
                        prevBrush.Color = Colors.LightGray;
                        prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    };
                    prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimPrev);

                    var sizeAnimPrev = new DoubleAnimation(24, TimeSpan.FromMilliseconds(300));
                    sizeAnimPrev.Completed += (s, e) =>
                    {
                        if (_currentHighlighted == null) return;
                        _currentHighlighted.FontSize = 24;
                        _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, null);
                    };
                    if (_currentHighlighted == null) return;
                    _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, sizeAnimPrev);
                }
                catch { }
            }

            // Animate current line to white and increase size
            try
            {
                var brush = tb.Foreground as SolidColorBrush;
                if (brush == null)
                {
                    brush = new SolidColorBrush(Colors.LightGray);
                    tb.Foreground = brush;
                }

                var colorAnim = new ColorAnimation(Colors.White, TimeSpan.FromMilliseconds(300));
                colorAnim.Completed += (s, e) =>
                {
                    brush.Color = Colors.White;
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

                var sizeAnim = new DoubleAnimation(30, TimeSpan.FromMilliseconds(300));
                sizeAnim.Completed += (s, e) =>
                {
                    tb.FontSize = 30;
                    tb.BeginAnimation(TextBlock.FontSizeProperty, null);
                };
                tb.BeginAnimation(TextBlock.FontSizeProperty, sizeAnim);

                _currentHighlighted = tb;

                // autoscroll: bring the tb into view (center)
                try
                {
                    var transform = tb.TransformToAncestor(lyricsPanel);
                    var point = transform.Transform(new Point(0, 0));
                    double target = point.Y;
                    double viewportHeight = lyricsScroll.ViewportHeight;
                    double offset = Math.Max(0, target - viewportHeight / 2);
                    lyricsScroll.ScrollToVerticalOffset(offset);
                }
                catch { }
            }
            catch { }
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
                    _visualizationWindow?.UpdateAudioSamples(samples);
                };
            }

            _outputDevice = new WaveOutEvent();
            if (OutputDeviceCombo.SelectedIndex >= 0)
            {
                try { _outputDevice.DeviceNumber = OutputDeviceCombo.SelectedIndex; } catch { }
            }

            // Output device reads from capturing provider (which wraps mixer)
            _outputDevice.Init(_capturingProvider);
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
            try
            {
                WholeLyricsPanel.Visibility = Visibility.Hidden;
                if (current == null) return;

                var res = await _lyricsService.GetCachedLyricsAsync(current.Artist.Replace(" - Topic", ""), current.Title, current.Duration);
                if (!res.Found)
                {
                    // fallback to live fetch
                    res = await _lyricsService.GetLyricsAsync(current.Artist, current.Title, current.Duration);
                }

                // prepare UI update
                Dispatcher.Invoke(() =>
                {
                    var lyricsPanel = FindName("LyricsStackPanel") as StackPanel;
                    if (lyricsPanel == null) return;
                    lyricsPanel.Children.Clear();
                    _currentSyncedLines.Clear();
                    _currentHighlighted = null;

                    if (res.Found)
                    {
                        if (res.HasSynced)
                        {
                            WholeLyricsPanel.Visibility = Visibility.Visible;
                            _currentSyncedLines = res.ParseSyncedLines();
                            foreach (var (time, text) in _currentSyncedLines)
                            {
                                var tb = new TextBlock { Text = text, Foreground = new SolidColorBrush(Colors.LightGray), FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0,4,0,4), MaxWidth = 780, TextWrapping = TextWrapping.Wrap};
                                lyricsPanel.Children.Add(tb);
                            }
                        }
                        else
                        {
                            WholeLyricsPanel.Visibility = Visibility.Visible;
                            // plain lyrics - split into lines
                            var lines = (res.PlainLyrics ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var l in lines)
                            {
                                var tb = new TextBlock { Text = l, Foreground = new SolidColorBrush(Colors.White), FontSize = 24, Margin = new Thickness(0,4,0,4), MaxWidth = 780, TextWrapping = TextWrapping.Wrap };
                                lyricsPanel.Children.Add(tb);
                            }
                        }
                    }
                    else
                    {
                        WholeLyricsPanel.Visibility = Visibility.Hidden;
                        var tb = new TextBlock { Text = "Lyrics not found.", Foreground = new SolidColorBrush(Colors.Gray), FontSize = 24 };
                        lyricsPanel.Children.Add(tb);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    var lyricsPanel = FindName("LyricsStackPanel") as StackPanel;
                    if (lyricsPanel == null) return;
                    lyricsPanel.Children.Clear();
                    var tb = new TextBlock { Text = "Error fetching lyrics: " + ex.Message, Foreground = Brushes.OrangeRed };
                    lyricsPanel.Children.Add(tb);
                    WholeLyricsPanel.Visibility = Visibility.Visible;
                });
            }
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

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog() { Filter = "Audio files|*.mp3;*.wav;*.m4a;*.flac|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        using var afr = new AudioFileReader(file);
                        var duration = afr.TotalTime;

                        // Default values
                        string title = System.IO.Path.GetFileNameWithoutExtension(file);
                        string artist = "Unknown";
                        var img = new Image();
                        img.Source = new BitmapImage(new Uri("pack://application:,,,/SRshortLogo.png"));

                        // Attempt to read metadata via TagLib
                        try
                        {
                            var tfile = TagLib.File.Create(file);
                            if (!string.IsNullOrWhiteSpace(tfile.Tag.Title)) title = tfile.Tag.Title;
                            if (tfile.Tag.Performers != null && tfile.Tag.Performers.Length > 0)
                            {
                                artist = string.Join(", ", tfile.Tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                                if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown";
                            }

                            // embedded picture -> use as thumbnail if present
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

                        var song = new Song(title, artist, img, duration, file);
                        Queue.Add(song);
                        AnimateListItem(song);

                        // Calculate loudness in background if normalization is enabled
                        var cfg = ConfigService.Instance.Current;
                        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
                        {
                            _ = Task.Run(() => CalculateSongLoudness(song));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unable to load file: " + ex.Message);
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
                    try
                    {
                        using var afr = new AudioFileReader(file);
                        var duration = afr.TotalTime;

                        // Default values
                        string title = System.IO.Path.GetFileNameWithoutExtension(file);
                        string artist = "Unknown";
                        var img = new Image();
                        img.Source = new BitmapImage(new Uri("pack://application:,,,/SRshortLogo.png"));

                        // Attempt to read metadata via TagLib
                        try
                        {
                            var tfile = TagLib.File.Create(file);
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
                            // ignore metadata failures
                        }

                        var song = new Song(title, artist, img, duration, file);
                        if (insertIndex <= Queue.Count) { Queue.Insert(insertIndex, song); insertIndex++; AnimateListItem(song); }
                        else { Queue.Add(song); AnimateListItem(song); }

                        // Calculate loudness in background if normalization is enabled
                        var cfg = ConfigService.Instance.Current;
                        if (cfg != null && cfg.NormalizeVolume && cfg.NormalizationActive)
                        {
                            _ = Task.Run(() => CalculateSongLoudness(song));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Unable to load file: " + ex.Message);
                    }
                }

                ComputeQueueTimings();
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

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
