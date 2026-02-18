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

        public MusicPlayer()
        {
            InitializeComponent();

            QueueList.ItemsSource = Queue;

            VolumeSlider.ValueChanged += (s, e) => { _volume = (float)VolumeSlider.Value; if (_currentVolProvider != null) _currentVolProvider.Volume = _volume; };

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

                    // raise tick event for external displays
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
                        }
                    }
                    catch { }

                    // update lyric highlighting and autoscroll
                    UpdateLyricHighlighting(_currentReader.CurrentTime);
                }
            }
        }

        private void UpdateLyricHighlighting(TimeSpan currentTime)
        {
            var lyricsPanel = FindName("LyricsStackPanel") as StackPanel;
            var lyricsScroll = FindName("LyricsScrollViewer") as ScrollViewer;
            if (lyricsPanel == null || lyricsScroll == null) return;

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
                // Stop and start next
                StopPlayback();
                var reader = new AudioFileReader(nextSong.songPath);
                StartPlaybackWithMixer(reader);

                // remove previous
                if (Queue.Count > 0) Queue.RemoveAt(0);
                if (Queue.Count > 0)
                {
                    NowTitle.Text = Queue[0].Title;
                    NowArtist.Text = Queue[0].Artist;
                    NowLength.Text = Queue[0].length;
                    if (Queue[0].thumbnail?.Source != null) NowThumbnail.Source = Queue[0].thumbnail.Source as BitmapSource;

                    TryApplyThumbnailGradient(Queue[0].thumbnail?.Source as BitmapSource);
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

            // create a volume wrapper for the reader and keep reference
            var sp = GetSampleProviderCompatible(reader);
            _currentVolProvider = new VolumeSampleProvider(sp) { Volume = _volume };
            _mixer.AddMixerInput(_currentVolProvider);

            _outputDevice = new WaveOutEvent();
            if (OutputDeviceCombo.SelectedIndex >= 0)
            {
                try { _outputDevice.DeviceNumber = OutputDeviceCombo.SelectedIndex; } catch { }
            }
            _outputDevice.Init(_mixer);
            _outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;
            _outputDevice.Play();

            _positionTimer.Start();

            // reflect play state
            PlayPauseButton.Content = "❚❚";
            ComputeQueueTimings();

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

            // Do not set reader.Volume - control volume via VolumeSampleProvider to avoid silent output
            _nextReader = new AudioFileReader(nextSong.songPath);

            // prepare next vol provider
            var nextSp = GetSampleProviderCompatible(_nextReader);
            _nextVolProvider = new VolumeSampleProvider(nextSp) { Volume = 0f };

            if (_mixer == null)
            {
                _mixer = new MixingSampleProvider(_format) { ReadFully = true };
                if (_currentVolProvider != null) _mixer.AddMixerInput(_currentVolProvider);
            }
            _mixer.AddMixerInput(_nextVolProvider);

            double cross = CrossfadeSlider.Value;
            if (cross <= 0) cross = 4;

            int steps = 40;
            int delay = Math.Max(10, (int)(cross * 1000 / steps));
            for (int i = 0; i < steps; i++)
            {
                float t = (i + 1f) / steps;
                // ramp up next, ramp down current using the volume providers
                try { _nextVolProvider.Volume = _volume * t; } catch { }
                try { if (_currentVolProvider != null) _currentVolProvider.Volume = _volume * (1f - t); } catch { }
                await Task.Delay(delay);
            }

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
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to load file: " + ex.Message);
            }
        }
    }
}
