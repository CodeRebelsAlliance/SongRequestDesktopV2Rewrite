using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using QRCoder;
using System.IO;

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
        private TextBlock _currentHighlighted = null;

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
            if (ConfigService.Instance?.Current != null)
            {
                ConfigService.Instance.Current.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(Config.PresentationFullscreen))
                    {
                        Dispatcher.Invoke(ApplyFullscreenSetting);
                    }
                    else if (e.PropertyName == nameof(Config.RequestUrl))
                    {
                        Dispatcher.Invoke(GenerateQRCode);
                    }
                };
            }

            // Generate QR code
            GenerateQRCode();

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

                // Show/hide Next Up panel based on queue count
                UpdateNextUpPanelVisibility(_next.Count > 0);
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

                // Show player panel, hide nothing playing panel
                ShowPlayerPanel();

                // Update now playing fields
                var titleTb = FindName("PresentationTitle") as TextBlock;
                var artistTb = FindName("PresentationArtist") as TextBlock;
                var thumb = FindName("PresentationThumbnail") as Image;
                var prog = FindName("PresentationProgress") as Slider;
                var elapsedTb = FindName("PresentationElapsed") as TextBlock;
                var remainingTb = FindName("PresentationRemaining") as TextBlock;
                var playerPanel = FindName("PlayerPanel") as Border;

                if (titleTb != null && e.Current != null) titleTb.Text = e.Current.Title;
                if (artistTb != null && e.Current != null) artistTb.Text = e.Current.Artist;
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
                    catch (Exception)
                    {
                        // If gradient extraction fails, keep default background
                    }
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

        private void ShowNothingPlayingState()
        {
            var playerPanel = FindName("PlayerPanel") as Border;
            var nothingPlayingPanel = FindName("NothingPlayingPanel") as Border;

            if (playerPanel != null && playerPanel.Visibility == Visibility.Visible)
            {
                // Animate player panel out
                var fadeOut = FindResource("FadeOut") as Storyboard;
                if (fadeOut != null)
                {
                    var sb = fadeOut.Clone();
                    sb.Completed += (s, e) =>
                    {
                        playerPanel.Visibility = Visibility.Collapsed;
                        playerPanel.Opacity = 0;
                    };
                    playerPanel.BeginStoryboard(sb);
                }
                else
                {
                    playerPanel.Visibility = Visibility.Collapsed;
                    playerPanel.Opacity = 0;
                }
            }

            if (nothingPlayingPanel != null && nothingPlayingPanel.Visibility != Visibility.Visible)
            {
                nothingPlayingPanel.Visibility = Visibility.Visible;
                nothingPlayingPanel.Opacity = 0;

                // Animate nothing playing panel in
                var fadeIn = FindResource("FadeIn") as Storyboard;
                if (fadeIn != null)
                {
                    var sb = fadeIn.Clone();
                    nothingPlayingPanel.BeginStoryboard(sb);
                }
                else
                {
                    nothingPlayingPanel.Opacity = 1;
                }
            }

            _currentSyncedLines.Clear();
            _lastSong = null;
            _currentLyricIndex = -1;
            _currentHighlighted = null;

            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();
            if (marqueeText != null) marqueeText.Text = "";
        }

        private void ShowPlayerPanel()
        {
            var playerPanel = FindName("PlayerPanel") as Border;
            var nothingPlayingPanel = FindName("NothingPlayingPanel") as Border;

            if (nothingPlayingPanel != null && nothingPlayingPanel.Visibility == Visibility.Visible)
            {
                // Animate nothing playing panel out
                var fadeOut = FindResource("FadeOut") as Storyboard;
                if (fadeOut != null)
                {
                    var sb = fadeOut.Clone();
                    sb.Completed += (s, e) =>
                    {
                        nothingPlayingPanel.Visibility = Visibility.Collapsed;
                        nothingPlayingPanel.Opacity = 0;
                    };
                    nothingPlayingPanel.BeginStoryboard(sb);
                }
                else
                {
                    nothingPlayingPanel.Visibility = Visibility.Collapsed;
                    nothingPlayingPanel.Opacity = 0;
                }
            }

            if (playerPanel != null && playerPanel.Visibility != Visibility.Visible)
            {
                playerPanel.Visibility = Visibility.Visible;
                playerPanel.Opacity = 0;

                // Animate player panel in
                var fadeIn = FindResource("FadeIn") as Storyboard;
                if (fadeIn != null)
                {
                    var sb = fadeIn.Clone();
                    playerPanel.BeginStoryboard(sb);
                }
                else
                {
                    playerPanel.Opacity = 1;
                }
            }
        }

        private void GenerateQRCode()
        {
            try
            {
                var requestUrl = ConfigService.Instance?.Current?.Address ?? "https://example.com/request";
                var requestUrlText = FindName("RequestUrlText") as TextBlock;
                if (requestUrlText != null)
                {
                    requestUrlText.Text = requestUrl;
                }

                using var qrGenerator = new QRCodeGenerator();
                using var qrCodeData = qrGenerator.CreateQrCode(requestUrl, QRCodeGenerator.ECCLevel.Q);
                using var qrCode = new PngByteQRCode(qrCodeData);
                var qrCodeBytes = qrCode.GetGraphic(20);

                var qrCodeImage = FindName("QRCodeImage") as Image;
                if (qrCodeImage != null)
                {
                    var bitmapImage = new BitmapImage();
                    using var mem = new MemoryStream(qrCodeBytes);
                    mem.Position = 0;
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = mem;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    qrCodeImage.Source = bitmapImage;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error generating QR code: {ex.Message}");
            }
        }

        private void ApplyFullscreenSetting()
        {
            var fullscreen = ConfigService.Instance?.Current?.PresentationFullscreen ?? false;
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

        private async System.Threading.Tasks.Task FetchAndDisplayLyricsAsync(Song song)
        {
            if (song == null) return;

            try
            {
                var result = await _lyricsService.GetLyricsAsync(song.Artist, song.Title, song.Duration);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (result.Found)
                    {
                        if (result.HasSynced)
                        {
                            _currentSyncedLines = result.ParseSyncedLines();
                            DisplaySyncedLyrics();
                        }
                        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
                        {
                            DisplayPlainLyrics(result.PlainLyrics);
                        }
                        else
                        {
                            DisplayNoLyrics();
                        }
                    }
                    else
                    {
                        DisplayNoLyrics();
                    }
                });
            }
            catch (Exception)
            {
                await Dispatcher.InvokeAsync(DisplayNoLyrics);
            }
        }

        private void DisplaySyncedLyrics()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsBorder = FindName("TimedLyricsBorder") as Border;
            var marqueeLyricsBorder = FindName("MarqueeLyricsBorder") as Border;
            var marqueeRow = FindName("MarqueeRow") as RowDefinition;

            if (timedLyricsStack == null) return;

            timedLyricsStack.Children.Clear();

            // Show timed lyrics, hide marquee
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(0);

            var marqueeText = FindName("MarqueeText") as TextBlock;
            if (marqueeText != null) marqueeText.Text = "";

            foreach (var (time, text) in _currentSyncedLines)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.LightGray),
                    Margin = new Thickness(0, 4, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                };
                timedLyricsStack.Children.Add(tb);
            }

            _currentLyricIndex = -1;
            _currentHighlighted = null;
        }

        private void DisplayPlainLyrics(string plainLyrics)
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsBorder = FindName("TimedLyricsBorder") as Border;
            var marqueeLyricsBorder = FindName("MarqueeLyricsBorder") as Border;
            var marqueeText = FindName("MarqueeText") as TextBlock;
            var marqueeRow = FindName("MarqueeRow") as RowDefinition;

            _currentSyncedLines.Clear();
            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();

            // Hide timed lyrics, show marquee
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(80);

            if (marqueeText != null)
            {
                var marqueeDisplay = plainLyrics.Replace("\n", " • ").Replace("\r", "");
                marqueeText.Text = marqueeDisplay;
            }
        }

        private void DisplayNoLyrics()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsBorder = FindName("TimedLyricsBorder") as Border;
            var marqueeLyricsBorder = FindName("MarqueeLyricsBorder") as Border;
            var marqueeText = FindName("MarqueeText") as TextBlock;
            var marqueeRow = FindName("MarqueeRow") as RowDefinition;

            _currentSyncedLines.Clear();

            // Show a message in timed lyrics area
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(0);

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
            _currentHighlighted = null;
        }

        private void UpdateLyricsDisplay(TimeSpan currentTime)
        {
            if (_currentSyncedLines.Count == 0) return;

            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsScroll = FindName("TimedLyricsScroll") as ScrollViewer;

            if (timedLyricsStack == null) return;

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

            if (newIndex < 0 || newIndex >= timedLyricsStack.Children.Count) return;
            if (newIndex == _currentLyricIndex) return;

            var tb = timedLyricsStack.Children[newIndex] as TextBlock;
            if (tb == null) return;

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
                        if (_currentHighlighted != null)
                        {
                            _currentHighlighted.FontSize = 24;
                            _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, null);
                        }
                    };
                    _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, sizeAnimPrev);
                }
                catch { }
            }

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
                _currentLyricIndex = newIndex;

                if (timedLyricsScroll != null)
                {
                    try
                    {
                        var transform = tb.TransformToAncestor(timedLyricsStack);
                        var point = transform.Transform(new System.Windows.Point(0, 0));
                        double target = point.Y;
                        double viewportHeight = timedLyricsScroll.ViewportHeight;
                        double offset = Math.Max(0, target - viewportHeight / 2);
                        timedLyricsScroll.ScrollToVerticalOffset(offset);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void UpdateNextUpPanelVisibility(bool hasItems)
        {
            var nextUpPanel = FindName("NextUpPanel") as Border;
            if (nextUpPanel == null) return;

            bool isCurrentlyVisible = nextUpPanel.Visibility == Visibility.Visible;

            if (hasItems && !isCurrentlyVisible)
            {
                // Show the panel with slide-in animation
                nextUpPanel.Visibility = Visibility.Visible;
                nextUpPanel.Opacity = 0;

                var slideIn = FindResource("SlideInFromRight") as Storyboard;
                if (slideIn != null)
                {
                    var sb = slideIn.Clone();
                    nextUpPanel.BeginStoryboard(sb);
                }
                else
                {
                    nextUpPanel.Opacity = 1;
                }

                // Switch to compact layout (title/artist below thumbnail)
                ApplyCompactLayout(false);
            }
            else if (!hasItems && isCurrentlyVisible)
            {
                // Hide the panel with slide-out animation
                var slideOut = FindResource("SlideOutToRight") as Storyboard;
                if (slideOut != null)
                {
                    var sb = slideOut.Clone();
                    sb.Completed += (s, e) =>
                    {
                        nextUpPanel.Visibility = Visibility.Collapsed;
                        nextUpPanel.Opacity = 0;
                    };
                    nextUpPanel.BeginStoryboard(sb);
                }
                else
                {
                    nextUpPanel.Visibility = Visibility.Collapsed;
                    nextUpPanel.Opacity = 0;
                }

                // Switch to expanded layout (title/artist beside thumbnail)
                ApplyCompactLayout(true);
            }
        }

        private void ApplyCompactLayout(bool compact)
        {
            var infoStackPanel = FindName("InfoStackPanel") as StackPanel;
            var thumbnailArea = FindName("ThumbnailArea") as Grid;
            var infoRow = FindName("InfoRow") as RowDefinition;

            if (infoStackPanel == null || thumbnailArea == null || infoRow == null) return;

            if (compact)
            {
                // Title/Artist beside thumbnail
                Grid.SetColumn(infoStackPanel, 1);
                Grid.SetRow(infoStackPanel, 0);
                infoStackPanel.VerticalAlignment = VerticalAlignment.Top;
                infoStackPanel.Margin = new Thickness(12, 15, 12, 6);
                infoRow.Height = new GridLength(0);
            }
            else
            {
                // Title/Artist below thumbnail (default)
                Grid.SetColumn(infoStackPanel, 1);
                Grid.SetRow(infoStackPanel, 1);
                infoStackPanel.VerticalAlignment = VerticalAlignment.Top;
                infoStackPanel.Margin = new Thickness(12, 15, 12, 6);
                infoRow.Height = GridLength.Auto;
            }
        }
    }
}
