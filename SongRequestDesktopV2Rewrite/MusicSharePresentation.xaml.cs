using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SongRequestDesktopV2Rewrite
{
    public partial class MusicSharePresentation : Window
    {
        private List<(TimeSpan Time, string Text)> _currentSyncedLines = new List<(TimeSpan, string)>();
        private int _currentLyricIndex = -1;
        private TextBlock? _currentHighlighted = null;

        // Timer for continuous lyrics update
        private System.Windows.Threading.DispatcherTimer? _lyricsTimer;
        private DateTime _songStartTime;
        private double _songElapsedSeconds;
        private double _songTotalSeconds;
        private bool _isPlaying = false;

        // Cache lyrics to avoid reloading on every metadata update
        private string? _lastLyricsContent = null;

        public MusicSharePresentation()
        {
            InitializeComponent();

            // Initialize lyrics update timer (60 FPS for smooth highlighting)
            _lyricsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
            };
            _lyricsTimer.Tick += LyricsTimer_Tick;
        }

        private void LyricsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying || _currentSyncedLines.Count == 0)
                return;

            // Calculate current playback position
            var elapsed = DateTime.Now - _songStartTime;
            var currentTime = TimeSpan.FromSeconds(_songElapsedSeconds) + elapsed;

            // Don't go past the total duration
            if (currentTime.TotalSeconds > _songTotalSeconds)
                currentTime = TimeSpan.FromSeconds(_songTotalSeconds);

            // Debug log occasionally with comparison
            if (_lyricsTimer?.IsEnabled == true && DateTime.Now.Millisecond < 20)
            {
                int matchingIndex = -1;
                for (int i = 0; i < _currentSyncedLines.Count; i++)
                {
                    if (currentTime >= _currentSyncedLines[i].Time)
                        matchingIndex = i;
                    else
                        break;
                }

                System.Diagnostics.Debug.WriteLine($"⏱️ Timer: {currentTime.TotalSeconds:F2}s (elapsed={_songElapsedSeconds:F2}+{elapsed.TotalSeconds:F2}), Match: {matchingIndex}, Current: {_currentLyricIndex}");

                if (matchingIndex >= 0 && matchingIndex < _currentSyncedLines.Count)
                {
                    System.Diagnostics.Debug.WriteLine($"   Should be at line {matchingIndex}: [{_currentSyncedLines[matchingIndex].Time.TotalSeconds:F2}s] \"{_currentSyncedLines[matchingIndex].Text}\"");
                }
            }

            UpdateLyricsDisplay(currentTime);
        }

        /// <summary>
        /// Update presentation with Music Share metadata
        /// </summary>
        public void UpdateFromMusicShare(MusicShareMetadata metadata)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Show player panel if hidden
                    ShowPlayerPanel();

                    // Update title and artist
                    PresentationTitle.Text = metadata.Title ?? "Unknown";
                    PresentationArtist.Text = metadata.Artist ?? "Unknown";

                    // Sync timing for lyrics highlighting
                    _songElapsedSeconds = metadata.ElapsedSeconds;
                    _songTotalSeconds = metadata.TotalSeconds;
                    _songStartTime = DateTime.Now;
                    _isPlaying = true;

                    System.Diagnostics.Debug.WriteLine($"🎵 Synced timing: elapsed={_songElapsedSeconds:F2}s, total={_songTotalSeconds:F2}s");

                    // Start lyrics timer if not already running
                    if (_lyricsTimer != null)
                    {
                        if (!_lyricsTimer.IsEnabled)
                        {
                            _lyricsTimer.Start();
                            System.Diagnostics.Debug.WriteLine("▶️ Started lyrics timer");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("⚠️ Lyrics timer is null!");
                    }

                    // Update progress bar and time
                    if (metadata.TotalSeconds > 0)
                    {
                        PresentationProgress.Maximum = 1;
                        PresentationProgress.Value = Math.Clamp(metadata.ElapsedSeconds / metadata.TotalSeconds, 0, 1);
                    }

                    var elapsed = TimeSpan.FromSeconds(metadata.ElapsedSeconds);
                    PresentationElapsed.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";

                    var remaining = TimeSpan.FromSeconds(metadata.TotalSeconds - metadata.ElapsedSeconds);
                    PresentationRemaining.Text = $"-{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";

                    // Update thumbnail if provided
                    if (!string.IsNullOrEmpty(metadata.ThumbnailData))
                    {
                        try
                        {
                            var bitmap = MusicShareService.Base64ToBitmapSource(metadata.ThumbnailData);
                            if (bitmap != null)
                            {
                                PresentationThumbnail.Source = bitmap;

                                // Update gradient background
                                try
                                {
                                    var (c1, c2) = ExtractTwoDarkColors(bitmap);
                                    var lg = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
                                    PlayerPanel.Background = lg;
                                }
                                catch
                                {
                                    // Keep default background if gradient extraction fails
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to update thumbnail: {ex.Message}");
                        }
                    }

                    // Update lyrics if provided - only reload if lyrics changed
                    if (!string.IsNullOrEmpty(metadata.Lyrics))
                    {
                        // Check if lyrics content has changed
                        if (_lastLyricsContent != metadata.Lyrics)
                        {
                            _lastLyricsContent = metadata.Lyrics;

                            if (metadata.HasSyncedLyrics)
                            {
                                ParseAndDisplaySyncedLyrics(metadata.Lyrics, metadata.ElapsedSeconds);
                            }
                            else
                            {
                                DisplayMarqueeLyrics(metadata.Lyrics);
                            }
                        }
                        // If lyrics haven't changed, just update the highlight position
                        else if (metadata.HasSyncedLyrics && _currentSyncedLines.Count > 0)
                        {
                            UpdateLyricsDisplay(TimeSpan.FromSeconds(metadata.ElapsedSeconds));
                        }
                    }
                    else if (_lastLyricsContent != null)
                    {
                        // Lyrics were removed
                        _lastLyricsContent = null;
                        DisplayNoLyrics();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error updating presentation: {ex.Message}");
                }
            });
        }

        private void ShowPlayerPanel()
        {
            if (WaitingPanel.Visibility == Visibility.Visible)
            {
                // Fade out waiting panel
                var fadeOut = FindResource("FadeOut") as Storyboard;
                if (fadeOut != null)
                {
                    var sb = fadeOut.Clone();
                    sb.Completed += (s, e) =>
                    {
                        WaitingPanel.Visibility = Visibility.Collapsed;
                        WaitingPanel.Opacity = 0;
                    };
                    WaitingPanel.BeginStoryboard(sb);
                }
                else
                {
                    WaitingPanel.Visibility = Visibility.Collapsed;
                }
            }

            if (PlayerPanel.Visibility != Visibility.Visible)
            {
                PlayerPanel.Visibility = Visibility.Visible;
                PlayerPanel.Opacity = 0;

                // Fade in player panel
                var fadeIn = FindResource("FadeIn") as Storyboard;
                if (fadeIn != null)
                {
                    PlayerPanel.BeginStoryboard(fadeIn.Clone());
                }
                else
                {
                    PlayerPanel.Opacity = 1;
                }
            }
        }

        private (Color, Color) ExtractTwoDarkColors(BitmapSource bmp)
        {
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[height * stride];
            formatted.CopyPixels(pixels, stride, 0);

            var counts = new Dictionary<int, int>();
            for (int y = 0; y < height; y += Math.Max(1, height / 40))
            {
                for (int x = 0; x < width; x += Math.Max(1, width / 40))
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx + 0];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (lum > 160) continue;

                    int rq = (r >> 3) & 0x1F;
                    int gq = (g >> 3) & 0x1F;
                    int bq = (b >> 3) & 0x1F;
                    int qKey = (rq << 10) | (gq << 5) | bq;
                    counts.TryGetValue(qKey, out int cv);
                    counts[qKey] = cv + 1;
                }
            }

            if (counts.Count == 0) return (Color.FromRgb(20, 20, 20), Color.FromRgb(45, 45, 45));

            var top = new List<int>(counts.Keys);
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

        private void ParseAndDisplaySyncedLyrics(string lrcContent, double currentSeconds)
        {
            try
            {
                _currentSyncedLines.Clear();

                var lines = lrcContent.Split('\n');
                foreach (var line in lines)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+):(\d+(?:\.\d+)?)\](.*)");
                    if (match.Success)
                    {
                        int minutes = int.Parse(match.Groups[1].Value);
                        double seconds = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                        string text = match.Groups[3].Value.Trim();

                        if (!string.IsNullOrEmpty(text))
                        {
                            var timestamp = TimeSpan.FromSeconds(minutes * 60 + seconds);
                            _currentSyncedLines.Add((timestamp, text));
                        }
                    }
                }

                if (_currentSyncedLines.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"🎼 Parsed {_currentSyncedLines.Count} lyrics. First: {_currentSyncedLines[0].Time.TotalSeconds:F2}s, Last: {_currentSyncedLines[^1].Time.TotalSeconds:F2}s");

                    DisplaySyncedLyrics();
                    UpdateLyricsDisplay(TimeSpan.FromSeconds(currentSeconds));
                }
                else
                {
                    DisplayNoLyrics();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Lyrics parsing error: {ex.Message}");
                DisplayNoLyrics();
            }
        }

        private void DisplaySyncedLyrics()
        {
            TimedLyricsStack.Children.Clear();

            TimedLyricsBorder.Visibility = Visibility.Visible;
            MarqueeLyricsBorder.Visibility = Visibility.Collapsed;
            MarqueeRow.Height = new GridLength(0);
            MarqueeText.Text = "";

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
                TimedLyricsStack.Children.Add(tb);
            }

            _currentLyricIndex = -1;
            _currentHighlighted = null;

            System.Diagnostics.Debug.WriteLine($"📝 Loaded {_currentSyncedLines.Count} synced lyric lines into UI");
        }

        private void DisplayMarqueeLyrics(string lyrics)
        {
            TimedLyricsBorder.Visibility = Visibility.Collapsed;
            MarqueeLyricsBorder.Visibility = Visibility.Visible;
            MarqueeRow.Height = GridLength.Auto;

            var cleanLyrics = System.Text.RegularExpressions.Regex.Replace(lyrics, @"\[\d+:\d+(?:\.\d+)?\]", "");
            MarqueeText.Text = cleanLyrics.Trim();

            var storyboard = FindResource("MarqueeStoryboard") as Storyboard;
            storyboard?.Begin(MarqueeText, true);

            _currentSyncedLines.Clear();
        }

        private void DisplayNoLyrics()
        {
            _currentSyncedLines.Clear();
            TimedLyricsBorder.Visibility = Visibility.Visible;
            MarqueeLyricsBorder.Visibility = Visibility.Collapsed;
            MarqueeRow.Height = new GridLength(0);

            TimedLyricsStack.Children.Clear();
            var tb = new TextBlock
            {
                Text = "No lyrics available",
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                Margin = new Thickness(0, 8, 0, 8),
                TextAlignment = TextAlignment.Center
            };
            TimedLyricsStack.Children.Add(tb);

            MarqueeText.Text = "";
            _currentHighlighted = null;
        }

        private void UpdateLyricsDisplay(TimeSpan currentTime)
        {
            if (_currentSyncedLines.Count == 0) return;

            int newIndex = -1;
            for (int i = 0; i < _currentSyncedLines.Count; i++)
            {
                if (currentTime >= _currentSyncedLines[i].Time)
                    newIndex = i;
                else
                    break;
            }

            if (newIndex < 0 || newIndex >= TimedLyricsStack.Children.Count)
                return;

            // Allow re-highlighting the same line (for initial display)
            if (newIndex == _currentLyricIndex)
                return;

            var tb = TimedLyricsStack.Children[newIndex] as TextBlock;
            if (tb == null) return;

            System.Diagnostics.Debug.WriteLine($"🎤 Highlighting lyric line {newIndex}: \"{tb.Text}\" at {currentTime.TotalSeconds:F2}s");

            // Fade out previous
            if (_currentHighlighted != null && _currentHighlighted != tb)
            {
                var prevBrush = _currentHighlighted.Foreground as SolidColorBrush ?? new SolidColorBrush(Colors.LightGray);
                _currentHighlighted.Foreground = prevBrush;

                var colorAnimPrev = new ColorAnimation(Colors.LightGray, TimeSpan.FromMilliseconds(300));
                prevBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimPrev);

                var sizeAnimPrev = new DoubleAnimation(24, TimeSpan.FromMilliseconds(300));
                _currentHighlighted.BeginAnimation(TextBlock.FontSizeProperty, sizeAnimPrev);
            }

            // Highlight current
            var brush = tb.Foreground as SolidColorBrush ?? new SolidColorBrush(Colors.LightGray);
            tb.Foreground = brush;

            var colorAnim = new ColorAnimation(Colors.White, TimeSpan.FromMilliseconds(300));
            brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

            var sizeAnim = new DoubleAnimation(30, TimeSpan.FromMilliseconds(300));
            tb.BeginAnimation(TextBlock.FontSizeProperty, sizeAnim);

            _currentHighlighted = tb;
            _currentLyricIndex = newIndex;

            // Auto-scroll
            try
            {
                if (TimedLyricsScroll != null && TimedLyricsStack != null)
                {
                    var transform = tb.TransformToAncestor(TimedLyricsStack);
                    var point = transform.Transform(new Point(0, 0));
                    double offset = Math.Max(0, point.Y - TimedLyricsScroll.ViewportHeight / 2);
                    TimedLyricsScroll.ScrollToVerticalOffset(offset);
                    System.Diagnostics.Debug.WriteLine($"📜 Scrolled to offset {offset:F2}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("⚠️ TimedLyricsScroll or TimedLyricsStack is null!");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Scroll error: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Stop lyrics timer
            if (_lyricsTimer != null)
            {
                _lyricsTimer.Stop();
                _lyricsTimer = null;
            }

            _isPlaying = false;
        }
    }
}
