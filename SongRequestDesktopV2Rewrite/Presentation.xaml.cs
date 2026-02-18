using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for Presentation.xaml
    /// </summary>
    public partial class Presentation : Window
    {
        private DispatcherTimer _timer;
        private ObservableCollection<Song> _next = new ObservableCollection<Song>();
        private MusicPlayer _musicPlayer;
        private LyricsService _lyricsService = new LyricsService();
        private List<(TimeSpan Time, string Text)> _currentSyncedLines = new List<(TimeSpan, string)>();
        private Song _lastSong = null;
        private int _currentLyricIndex = -1;

        public Presentation(MusicPlayer musicPlayer)
        {
            InitializeComponent();

            _musicPlayer = musicPlayer;

            var nextCtrl = FindName("NextThreeItems") as ItemsControl;
            if (nextCtrl != null) nextCtrl.ItemsSource = _next;

            // subscribe to music player events
            _musicPlayer.NowPlayingTick += MusicPlayer_NowPlayingTick;
            _musicPlayer.QueueChanged += MusicPlayer_QueueChanged;

            // timer to update marquee animation state if needed
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) => { /* keep UI alive for animations */ };
            _timer.Start();

            // initial population of next items
            MusicPlayer_QueueChanged(this, EventArgs.Empty);

            // Apply fullscreen setting
            ApplyFullscreenSetting();

            // Listen for config changes
            ConfigService.Instance.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Config.PresentationFullscreen))
                {
                    Dispatcher.Invoke(ApplyFullscreenSetting);
                }
            };

            // Set initial "nothing playing" state
            ShowNothingPlayingState();
        }

        private void MusicPlayer_QueueChanged(object sender, EventArgs e)
        {
            // update next items on UI thread
            Dispatcher.Invoke(() =>
            {
                _next.Clear();
                int added = 0;
                foreach (var s in _musicPlayer.Queue)
                {
                    if (added == 0) { added++; continue; } // skip current
                    _next.Add(s);
                    added++;
                    if (added > 3) break;
                }
            });
        }

        private void MusicPlayer_NowPlayingTick(object sender, MusicPlayer.NowPlayingEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Check if nothing is playing
                if (e.Current == null)
                {
                    ShowNothingPlayingState();
                    return;
                }

                // Update now playing fields
                var titleTb = FindName("PresentationTitle") as TextBlock;
                var artistTb = FindName("PresentationArtist") as TextBlock;
                var thumb = FindName("PresentationThumbnail") as Image;
                var prog = FindName("PresentationProgress") as Slider;
                var elapsedTb = FindName("PresentationElapsed") as TextBlock;
                var remainingTb = FindName("PresentationRemaining") as TextBlock;
                var playerPanel = FindName("PlayerPanel") as Border;
                var nothingPlayingPanel = FindName("NothingPlayingPanel") as Border;

                // Show player panel, hide nothing playing panel
                if (playerPanel != null) playerPanel.Visibility = Visibility.Visible;
                if (nothingPlayingPanel != null) nothingPlayingPanel.Visibility = Visibility.Collapsed;

                if (titleTb != null) titleTb.Text = e.Current.Title;
                if (artistTb != null) artistTb.Text = e.Current.Artist;
                if (thumb != null && e.Current?.thumbnail?.Source != null) thumb.Source = e.Current.thumbnail.Source as ImageSource;

                if (prog != null && e.TotalTime.TotalSeconds > 0)
                {
                    prog.Value = Math.Clamp(e.CurrentTime.TotalSeconds / e.TotalTime.TotalSeconds, 0, 1);
                }

                if (elapsedTb != null) elapsedTb.Text = e.CurrentTime.ToString(@"mm\:ss");
                if (remainingTb != null) remainingTb.Text = (e.TotalTime - e.CurrentTime).ToString(@"mm\:ss");

                // Apply player panel gradient from thumbnail
                if (playerPanel != null && thumb?.Source is BitmapSource bmp)
                {
                    try
                    {
                        var (c1, c2) = ExtractTwoDarkColors(bmp);
                        var lg = new LinearGradientBrush(c1, c2, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                        playerPanel.Background = lg;
                    }
                    catch { }
                }

                // Fetch lyrics if this is a new song
                if (_lastSong != e.Current)
                {
                    _lastSong = e.Current;
                    _ = FetchAndDisplayLyricsAsync(e.Current);
                }

                // Update lyrics display based on current time
                UpdateLyricsDisplay(e.CurrentTime);
            });
        }

        // Public API for other windows to set display initially
        public void SetNowPlaying(Song s)
        {
            if (s == null) return;
            MusicPlayer_NowPlayingTick(this, new MusicPlayer.NowPlayingEventArgs { Current = s, CurrentTime = TimeSpan.Zero, TotalTime = s.Duration });
        }

        public void SetNextItems(System.Collections.Generic.IEnumerable<Song> items)
        {
            _next.Clear();
            foreach (var it in items) _next.Add(it);
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

            var counts = new System.Collections.Generic.Dictionary<int, int>();
            for (int y = 0; y < height; y += Math.Max(1, height / 40))
            {
                for (int x = 0; x < width; x += Math.Max(1, width / 40))
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx + 0];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (lum > 160) continue; // skip bright pixels

                    int rq = (r >> 3) & 0x1F;
                    int gq = (g >> 3) & 0x1F;
                    int bq = (b >> 3) & 0x1F;
                    int qKey = (rq << 10) | (gq << 5) | bq;
                    counts.TryGetValue(qKey, out int cv);
                    counts[qKey] = cv + 1;
                }
            }

            if (counts.Count == 0) return (Color.FromRgb(20, 20, 20), Color.FromRgb(45, 45, 45));

            var top = new System.Collections.Generic.List<int>(counts.Keys);
            top.Sort((a, b) => counts[b].CompareTo(counts[a]));
            int k1 = top[0];
            int k2 = top.Count > 1 ? top[1] : k1;

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
            var c2 = ToColor(k2 == k1 ? top[Math.Min(2, top.Count - 1)] : k2);
            return (c1, c2);
        }

        private void ApplyFullscreenSetting()
        {
            var fullscreen = ConfigService.Instance.Current.PresentationFullscreen;
            if (fullscreen)
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                ResizeMode = ResizeMode.NoResize;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.CanResize;
            }
        }

        private void ShowNothingPlayingState()
        {
            var playerPanel = FindName("PlayerPanel") as Border;
            var nothingPlayingPanel = FindName("NothingPlayingPanel") as Border;

            if (playerPanel != null) playerPanel.Visibility = Visibility.Collapsed;
            if (nothingPlayingPanel != null) nothingPlayingPanel.Visibility = Visibility.Visible;

            // Clear lyrics
            _currentSyncedLines.Clear();
            _lastSong = null;
            _currentLyricIndex = -1;

            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();
            if (marqueeText != null) marqueeText.Text = "";
        }

        private async System.Threading.Tasks.Task FetchAndDisplayLyricsAsync(Song song)
        {
            if (song == null) return;

            try
            {
                var result = await _lyricsService.GetLyricsAsync(song.Artist, song.Title, song.Duration);

                // Update UI on dispatcher thread
                await Dispatcher.InvokeAsync(() =>
                {
                    if (result.Found)
                    {
                        if (result.HasSynced)
                        {
                            // Display synced lyrics
                            _currentSyncedLines = result.ParseSyncedLines();
                            DisplaySyncedLyrics();
                        }
                        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
                        {
                            // Display plain lyrics as marquee
                            DisplayPlainLyrics(result.PlainLyrics);
                        }
                        else
                        {
                            // No lyrics available
                            DisplayNoLyrics();
                        }
                    }
                    else
                    {
                        // Lyrics not found
                        DisplayNoLyrics();
                    }
                });
            }
            catch
            {
                // On error, show no lyrics message
                await Dispatcher.InvokeAsync(DisplayNoLyrics);
            }
        }

        private void DisplaySyncedLyrics()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            if (timedLyricsStack == null) return;

            // Clear previous lyrics
            timedLyricsStack.Children.Clear();
            if (marqueeText != null) marqueeText.Text = "";

            // Add each line as a TextBlock
            foreach (var line in _currentSyncedLines)
            {
                var tb = new TextBlock
                {
                    Text = line.Text,
                    FontSize = 20,
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    Margin = new Thickness(0, 8, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                };
                timedLyricsStack.Children.Add(tb);
            }

            _currentLyricIndex = -1;
        }

        private void DisplayPlainLyrics(string plainLyrics)
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            // Clear synced lyrics
            _currentSyncedLines.Clear();
            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();

            // Display plain lyrics in marquee
            if (marqueeText != null)
            {
                // Replace newlines with separator for marquee display
                var marqueeDisplay = plainLyrics.Replace("\n", " • ").Replace("\r", "");
                marqueeText.Text = marqueeDisplay;
            }
        }

        private void DisplayNoLyrics()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            _currentSyncedLines.Clear();
            if (timedLyricsStack != null)
            {
                timedLyricsStack.Children.Clear();
                var tb = new TextBlock
                {
                    Text = "No lyrics available for this song",
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    Margin = new Thickness(0, 8, 0, 8),
                    TextAlignment = TextAlignment.Center
                };
                timedLyricsStack.Children.Add(tb);
            }

            if (marqueeText != null) marqueeText.Text = "";
        }

        private void UpdateLyricsDisplay(TimeSpan currentTime)
        {
            if (_currentSyncedLines.Count == 0) return;

            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsScroll = FindName("TimedLyricsScroll") as ScrollViewer;

            if (timedLyricsStack == null) return;

            // Find the current line based on time
            int newIndex = -1;
            for (int i = 0; i < _currentSyncedLines.Count; i++)
            {
                if (currentTime >= _currentSyncedLines[i].Time)
                {
                    newIndex = i;
                }
                else
                {
                    break;
                }
            }

            // Update highlighting if index changed
            if (newIndex != _currentLyricIndex && newIndex >= 0 && newIndex < timedLyricsStack.Children.Count)
            {
                // Remove old highlight
                if (_currentLyricIndex >= 0 && _currentLyricIndex < timedLyricsStack.Children.Count)
                {
                    var oldTb = timedLyricsStack.Children[_currentLyricIndex] as TextBlock;
                    if (oldTb != null)
                    {
                        oldTb.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                        oldTb.FontWeight = FontWeights.Normal;
                        oldTb.FontSize = 20;
                    }
                }

                // Add new highlight
                var newTb = timedLyricsStack.Children[newIndex] as TextBlock;
                if (newTb != null)
                {
                    newTb.Foreground = Brushes.White;
                    newTb.FontWeight = FontWeights.Bold;
                    newTb.FontSize = 24;

                    // Auto-scroll to the current line
                    if (timedLyricsScroll != null)
                    {
                        newTb.BringIntoView();
                    }
                }

                _currentLyricIndex = newIndex;
            }
        }
    }
}
