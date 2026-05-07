using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using QRCoder;
using System.IO;
using Newtonsoft.Json;

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
        private const double BaseLyricFontSize = 34;
        private const double ActiveLyricFontSize = 42;
        private const int LyricAnimationDurationMs = 420;
        private static readonly Color InactiveLyricColor = Color.FromRgb(195, 195, 195);
        private static readonly Color ActiveLyricColor = Colors.White;
        private const double InactiveLyricOpacity = 0.82;
        private const double ActiveLyricOpacity = 1.0;
        private readonly object _lyricsCacheLock = new object();
        private readonly Dictionary<string, LyricsResult> _lyricsCache = new Dictionary<string, LyricsResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<LyricsResult>> _lyricsInFlight = new Dictionary<string, Task<LyricsResult>>(StringComparer.OrdinalIgnoreCase);
        private int _lyricsRequestVersion;
        private bool _isLyricsFocusLayout;

        public static readonly DependencyProperty SmoothScrollOffsetProperty =
            DependencyProperty.RegisterAttached(
                "SmoothScrollOffset",
                typeof(double),
                typeof(Presentation),
                new PropertyMetadata(0.0, OnSmoothScrollOffsetChanged));

        private static void OnSmoothScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

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

                // Warm up lyrics for the upcoming song to reduce transition latency.
                PrefetchNextSongLyrics();
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
                var prog = FindName("PresentationProgress") as ProgressBar;
                var elapsedTb = FindName("PresentationElapsed") as TextBlock;
                var remainingTb = FindName("PresentationRemaining") as TextBlock;
                var playerPanel = FindName("PlayerPanel") as Border;

                if (titleTb != null && e.Current != null) titleTb.Text = e.Current.Title;
                if (artistTb != null && e.Current != null) artistTb.Text = e.Current.Artist;
                if (thumb != null && e.Current?.thumbnail?.Source != null) thumb.Source = e.Current.thumbnail.Source as ImageSource;

                if (prog != null && e.TotalTime.TotalSeconds > 0)
                {
                    double targetProgress = Math.Clamp(e.CurrentTime.TotalSeconds / e.TotalTime.TotalSeconds, 0, 1);
                    var progressAnim = new DoubleAnimation(targetProgress, TimeSpan.FromMilliseconds(240))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    prog.BeginAnimation(RangeBase.ValueProperty, progressAnim, HandoffBehavior.SnapshotAndReplace);
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
                    SetLyricsFocusLayout(false);
                    _lyricsRequestVersion++;
                    int requestVersion = _lyricsRequestVersion;
                    _ = FetchAndDisplayLyricsAsync(e.Current, requestVersion);
                    PrefetchNextSongLyrics();
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

        private string BuildLyricsKey(Song song)
        {
            if (song == null) return string.Empty;
            return $"{song.songPath ?? string.Empty}|{song.Artist ?? string.Empty}|{song.Title ?? string.Empty}|{(int)Math.Round(song.Duration.TotalSeconds)}";
        }

        private Task<LyricsResult> GetOrStartLyricsFetchTask(Song song)
        {
            var key = BuildLyricsKey(song);

            lock (_lyricsCacheLock)
            {
                if (_lyricsCache.TryGetValue(key, out var cached))
                {
                    return Task.FromResult(cached);
                }

                if (_lyricsInFlight.TryGetValue(key, out var existingTask))
                {
                    return existingTask;
                }

                var task = FetchLyricsAndCacheAsync(song, key);
                _lyricsInFlight[key] = task;
                return task;
            }
        }

        private async Task<LyricsResult> FetchLyricsAndCacheAsync(Song song, string key)
        {
            try
            {
                var query = LyricsQueryNormalizer.Build(song);
                var result = await _lyricsService.GetLyricsAsync(query.Artist, query.Title, song.Duration);
                lock (_lyricsCacheLock)
                {
                    _lyricsCache[key] = result;
                    _lyricsInFlight.Remove(key);
                }
                return result;
            }
            catch
            {
                lock (_lyricsCacheLock)
                {
                    _lyricsInFlight.Remove(key);
                }
                throw;
            }
        }

        private void PrefetchNextSongLyrics()
        {
            if (_musicPlayer?.Queue == null || _musicPlayer.Queue.Count <= 1) return;
            var nextSong = _musicPlayer.Queue[1];
            if (nextSong == null) return;

            _ = GetOrStartLyricsFetchTask(nextSong);
        }

        private void SetLyricsLoadingVisible(bool visible)
        {
            var loadingPanel = FindName("LyricsLoadingPanel") as UIElement;
            if (loadingPanel != null)
            {
                loadingPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void DisplayLyricsLoading()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsBorder = FindName("TimedLyricsBorder") as Border;
            var marqueeLyricsBorder = FindName("MarqueeLyricsBorder") as Border;
            var marqueeRow = FindName("MarqueeRow") as RowDefinition;
            var lyricsContentRow = FindName("LyricsContentRow") as RowDefinition;
            var marqueeText = FindName("MarqueeText") as TextBlock;

            _currentSyncedLines.Clear();
            _currentLyricIndex = -1;
            _currentHighlighted = null;

            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(0);
            if (lyricsContentRow != null) lyricsContentRow.Height = new GridLength(1, GridUnitType.Star);
            if (marqueeText != null) marqueeText.Text = "";

            SetLyricsLoadingVisible(true);
            SetLyricsFocusLayout(false);
            SetLyricsProviderLabel("Lyrics: Loading...");
        }

        private void ApplyLyricsResult(LyricsResult result)
        {
            if (result != null && result.Found)
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
        }

        private async Task FetchAndDisplayLyricsAsync(Song song, int requestVersion)
        {
            if (song == null) return;

            var songKey = BuildLyricsKey(song);
            var fetchTask = GetOrStartLyricsFetchTask(song);

            if (!fetchTask.IsCompleted)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(350);
                    if (requestVersion != _lyricsRequestVersion) return;
                    if (_lastSong == null || BuildLyricsKey(_lastSong) != songKey) return;
                    if (fetchTask.IsCompleted) return;

                    await Dispatcher.InvokeAsync(DisplayLyricsLoading);
                });
            }

            LyricsResult result;
            string providerLabel = "Lyrics: LRCLIB";
            try
            {
                result = await fetchTask;
            }
            catch
            {
                result = new LyricsResult { Found = false };
            }

            var fallbackEnabled = ConfigService.Instance.Current?.UseCaptionLyricsFallback ?? true;
            if (fallbackEnabled
                && (!result.Found || (!result.HasSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
                && TryGetCachedYoutubeSubtitleFallback(song.songPath, out var timedSubtitleFallback, out var plainSubtitleFallback))
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
                if (requestVersion != _lyricsRequestVersion) return;
                if (_lastSong == null || BuildLyricsKey(_lastSong) != songKey) return;
                SetLyricsProviderLabel(providerLabel);
                ApplyLyricsResult(result);
            });
        }

        private void DisplaySyncedLyrics()
        {
            var timedLyricsStack = FindName("TimedLyricsStack") as StackPanel;
            var timedLyricsBorder = FindName("TimedLyricsBorder") as Border;
            var marqueeLyricsBorder = FindName("MarqueeLyricsBorder") as Border;
            var marqueeRow = FindName("MarqueeRow") as RowDefinition;
            var lyricsContentRow = FindName("LyricsContentRow") as RowDefinition;

            if (timedLyricsStack == null) return;

            timedLyricsStack.Children.Clear();

            // Show timed lyrics, hide marquee
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(0);
            if (lyricsContentRow != null) lyricsContentRow.Height = new GridLength(1, GridUnitType.Star);
            SetLyricsLoadingVisible(false);
            SetLyricsFocusLayout(false);

            var marqueeText = FindName("MarqueeText") as TextBlock;
            if (marqueeText != null) marqueeText.Text = "";

            foreach (var (time, text) in _currentSyncedLines)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = BaseLyricFontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(InactiveLyricColor),
                    Margin = new Thickness(0, 10, 0, 10),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    LineHeight = 44,
                    Opacity = InactiveLyricOpacity
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
            var lyricsContentRow = FindName("LyricsContentRow") as RowDefinition;

            _currentSyncedLines.Clear();
            if (timedLyricsStack != null) timedLyricsStack.Children.Clear();

            // Hide timed lyrics, show marquee
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Visible;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(80);
            if (lyricsContentRow != null) lyricsContentRow.Height = new GridLength(0);
            SetLyricsLoadingVisible(false);
            SetLyricsFocusLayout(false);

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
            var lyricsContentRow = FindName("LyricsContentRow") as RowDefinition;

            _currentSyncedLines.Clear();

            // Hide lyrics area completely when no lyrics are available.
            if (timedLyricsBorder != null) timedLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeLyricsBorder != null) marqueeLyricsBorder.Visibility = Visibility.Collapsed;
            if (marqueeRow != null) marqueeRow.Height = new GridLength(0);
            if (lyricsContentRow != null) lyricsContentRow.Height = new GridLength(0);
            SetLyricsLoadingVisible(false);
            SetLyricsFocusLayout(true);

            if (timedLyricsStack != null)
            {
                timedLyricsStack.Children.Clear();
            }

            if (marqueeText != null) marqueeText.Text = "";
            _currentHighlighted = null;
            SetLyricsProviderLabel("");
        }

        private void SetLyricsProviderLabel(string text)
        {
            var providerText = FindName("LyricsProviderText") as TextBlock;
            if (providerText != null)
            {
                providerText.Text = text;
            }
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
                if (!File.Exists(cachePath)) return false;

                var json = File.ReadAllText(cachePath);
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
                        prevBrush = new SolidColorBrush(InactiveLyricColor);
                        _currentHighlighted.Foreground = prevBrush;
                    }

                    var colorAnimPrev = new ColorAnimation(InactiveLyricColor, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    colorAnimPrev.Completed += (s, e) =>
                    {
                        prevBrush.Color = InactiveLyricColor;
                        prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    };
                    prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimPrev);

                    var sizeAnimPrev = new DoubleAnimation(BaseLyricFontSize, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sizeAnimPrev.Completed += (s, e) =>
                    {
                        if (_currentHighlighted != null)
                        {
                            _currentHighlighted.FontSize = BaseLyricFontSize;
                            _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, null);
                        }
                    };
                    _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, sizeAnimPrev);

                    var opacityAnimPrev = new DoubleAnimation(InactiveLyricOpacity, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    _currentHighlighted.BeginAnimation(TextBlock.OpacityProperty, opacityAnimPrev);
                }
                catch { }
            }

            try
            {
                var brush = tb.Foreground as SolidColorBrush;
                if (brush == null)
                {
                    brush = new SolidColorBrush(InactiveLyricColor);
                    tb.Foreground = brush;
                }

                var colorAnim = new ColorAnimation(ActiveLyricColor, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                colorAnim.Completed += (s, e) =>
                {
                    brush.Color = ActiveLyricColor;
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

                var sizeAnim = new DoubleAnimation(ActiveLyricFontSize, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                sizeAnim.Completed += (s, e) =>
                {
                    tb.FontSize = ActiveLyricFontSize;
                    tb.BeginAnimation(TextBlock.FontSizeProperty, null);
                };
                tb.BeginAnimation(TextBlock.FontSizeProperty, sizeAnim);
                tb.BeginAnimation(TextBlock.OpacityProperty, new DoubleAnimation(ActiveLyricOpacity, TimeSpan.FromMilliseconds(LyricAnimationDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

                _currentHighlighted = tb;
                _currentLyricIndex = newIndex;

                if (timedLyricsScroll != null)
                {
                    try
                    {
                        var transform = tb.TransformToAncestor(timedLyricsStack);
                        var point = transform.Transform(new System.Windows.Point(0, 0));
                        double target = point.Y + (tb.ActualHeight / 2.0);
                        double viewportHeight = timedLyricsScroll.ViewportHeight;
                        double offset = Math.Max(0, target - (viewportHeight / 2.0));
                        timedLyricsScroll.SetValue(SmoothScrollOffsetProperty, timedLyricsScroll.VerticalOffset);

                        var scrollAnim = new DoubleAnimation(offset, TimeSpan.FromMilliseconds(LyricAnimationDurationMs + 120))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        timedLyricsScroll.BeginAnimation(SmoothScrollOffsetProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SetLyricsFocusLayout(bool enabled)
        {
            if (_isLyricsFocusLayout == enabled) return;
            _isLyricsFocusLayout = enabled;
            ApplyAdaptivePresentationLayout(true);
        }

        private void ApplyAdaptivePresentationLayout(bool animate)
        {
            var thumbnailBorder = FindName("ThumbnailBorder") as Border;
            var infoStackPanel = FindName("InfoStackPanel") as StackPanel;
            var titleBorder = FindName("TitleBadgeBorder") as Border;
            var artistBorder = FindName("ArtistBadgeBorder") as Border;
            var titleText = FindName("PresentationTitle") as TextBlock;
            var artistText = FindName("PresentationArtist") as TextBlock;
            var progressLyricsGrid = FindName("ProgressLyricsGrid") as Grid;

            if (thumbnailBorder == null || infoStackPanel == null || progressLyricsGrid == null) return;

            double targetThumbnailHeight;
            Thickness targetInfoMargin;
            Thickness targetProgressMargin;

            if (_isLyricsFocusLayout)
            {
                Grid.SetRow(infoStackPanel, 1);
                Grid.SetColumn(infoStackPanel, 0);
                Grid.SetColumnSpan(infoStackPanel, 2);

                Grid.SetRow(thumbnailBorder, 0);
                Grid.SetColumn(thumbnailBorder, 0);
                Grid.SetColumnSpan(thumbnailBorder, 2);

                thumbnailBorder.HorizontalAlignment = HorizontalAlignment.Center;
                infoStackPanel.HorizontalAlignment = HorizontalAlignment.Center;
                infoStackPanel.VerticalAlignment = VerticalAlignment.Top;

                if (titleBorder != null) titleBorder.HorizontalAlignment = HorizontalAlignment.Center;
                if (artistBorder != null) artistBorder.HorizontalAlignment = HorizontalAlignment.Center;
                if (titleText != null) titleText.TextAlignment = TextAlignment.Center;
                if (artistText != null) artistText.TextAlignment = TextAlignment.Center;

                targetThumbnailHeight = 360;
                targetInfoMargin = new Thickness(12, 8, 12, 0);
                targetProgressMargin = new Thickness(12, 26, 12, 12);
            }
            else
            {
                Grid.SetRow(thumbnailBorder, 0);
                Grid.SetColumn(thumbnailBorder, 0);
                Grid.SetColumnSpan(thumbnailBorder, 1);
                thumbnailBorder.HorizontalAlignment = HorizontalAlignment.Left;

                // In normal (lyrics) mode, always place title/artist to the right of the artwork.
                Grid.SetRow(infoStackPanel, 0);
                Grid.SetColumn(infoStackPanel, 1);
                Grid.SetColumnSpan(infoStackPanel, 1);
                infoStackPanel.HorizontalAlignment = HorizontalAlignment.Left;
                targetInfoMargin = new Thickness(12, 15, 12, 6);

                infoStackPanel.VerticalAlignment = VerticalAlignment.Top;
                if (titleBorder != null) titleBorder.HorizontalAlignment = HorizontalAlignment.Left;
                if (artistBorder != null) artistBorder.HorizontalAlignment = HorizontalAlignment.Left;
                if (titleText != null) titleText.TextAlignment = TextAlignment.Left;
                if (artistText != null) artistText.TextAlignment = TextAlignment.Left;

                targetThumbnailHeight = 270;
                targetProgressMargin = new Thickness(12, 12, 12, 12);
            }

            if (animate)
            {
                AnimateDouble(thumbnailBorder, FrameworkElement.HeightProperty, targetThumbnailHeight, 420);
                AnimateThickness(infoStackPanel, FrameworkElement.MarginProperty, targetInfoMargin, 420);
                AnimateThickness(progressLyricsGrid, FrameworkElement.MarginProperty, targetProgressMargin, 420);
            }
            else
            {
                thumbnailBorder.Height = targetThumbnailHeight;
                infoStackPanel.Margin = targetInfoMargin;
                progressLyricsGrid.Margin = targetProgressMargin;
            }
        }

        private static void AnimateDouble(FrameworkElement target, DependencyProperty property, double to, int durationMs)
        {
            if (target == null) return;
            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateThickness(FrameworkElement target, DependencyProperty property, Thickness to, int durationMs)
        {
            if (target == null) return;
            var animation = new ThicknessAnimation(to, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
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

            }

            // Keep lyrics layout stable; NextUp visibility only controls panel itself.
        }

    }
}
