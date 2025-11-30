using System;
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
                // Update now playing fields
                var titleTb = FindName("PresentationTitle") as TextBlock;
                var artistTb = FindName("PresentationArtist") as TextBlock;
                var thumb = FindName("PresentationThumbnail") as Image;
                var prog = FindName("PresentationProgress") as Slider;
                var elapsedTb = FindName("PresentationElapsed") as TextBlock;
                var remainingTb = FindName("PresentationRemaining") as TextBlock;

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
                var playerPanel = FindName("PlayerPanel") as Border;
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

                // Optionally update highlighted timed lyric based on e.CurrentTime (not implemented parsing in this demo)
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
    }
}
