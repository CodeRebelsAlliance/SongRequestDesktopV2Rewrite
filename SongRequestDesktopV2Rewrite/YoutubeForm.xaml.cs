using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for YoutubeForm.xaml
    /// </summary>
    public partial class YoutubeForm : Window
    {
        private readonly DispatcherTimer _clockTimer;
        private DispatcherTimer _refreshTimer;
        private MusicPlayer _musicPlayer;
        private YoutubeService _youTubeService;
        public static string downloadPath = @"data\downloadedvideos\";
        public string secID => ConfigService.Instance.Current.BearerToken;
        private bool currently_fetching = false;
        public static bool broadcast_player = false;
        public static bool sendin_enabled = true;
        private List<string> wordsToCheck;
        private bool closing = false;
        public int ConcurrentTaskNumber => ConfigService.Instance.Current.Threads;
        public HashSet<string> fetchedYtids = new HashSet<string>();
        public HashSet<string> fetchedBlacklist = new HashSet<string>();
        private Dictionary<string, VideoData> fetchedVideoData = new Dictionary<string, VideoData>();
        public int refresh_seconds => ConfigService.Instance.Current.FetchingTimer;
        public YoutubeForm(IReadOnlyList<System.Net.Cookie> cookies)
        {
            InitializeComponent();
            _youTubeService = new YoutubeService(cookies);
            // start clock
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            // Example: clear loading states initially
            UIHelpers.SetIsLoading(VideoListHost, false);

            _musicPlayer = new MusicPlayer();
            UrlTextBox.TextChanged += UrlTextBoxOnTextChanged;
            void UrlTextBoxOnTextChanged(object sender, TextChangedEventArgs e)
            {
                SubmitUrlButton.IsEnabled = UrlTextBox.Text.Length > 0;
            }

            // Setup periodic refresh timer using config value
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(1, refresh_seconds))
            };
            _refreshTimer.Tick += async (s, e) =>
            {
                try
                {
                    await FetchData();
                }
                catch (Exception ex)
                {
                    AppendConsoleText("Error during periodic refresh: " + ex.Message);
                }
            };
            _refreshTimer.Start();

            // Update timer interval when config changes
            try
            {
                var cfg = ConfigService.Instance.Current;
                if (cfg != null)
                {
                    cfg.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(cfg.FetchingTimer))
                        {
                            Dispatcher.Invoke(() => _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, refresh_seconds)));
                        }
                    };
                }
            }
            catch { }
        }

        private async void SubmitUrlButton_Click(object sender, EventArgs e)
        {
            string videoID = YoutubeService.ExtractVideoId(UrlTextBox.Text);
            if (videoID != null && videoID.Length == 11)
            {
                await SendOtherRequest("add", videoID);
                FetchData();
                UrlTextBox.Text = "";
            }
        }

        private async void FetchDataButton_Click(object sender, EventArgs e)
        {
            FetchData();
        }

        private void SettingsButton_Click(object sender, EventArgs e)
        {
            Settings settingsWindow = new Settings();
            settingsWindow.Show();
        }

        private void SwitchListButton_Click(object sender, EventArgs e)
        {
            if (VideoListPanel.IsVisible)
            {
                ListViewer.Visibility = Visibility.Hidden;
                BlackListViewer.Visibility = Visibility.Visible;
                ViewLabel.Text = "Blacklist";
            }
            else
            {
                ListViewer.Visibility = Visibility.Visible;
                BlackListViewer.Visibility = Visibility.Hidden;
                ViewLabel.Text = "Normal List";
            }
        }

        private async void MusicPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            _musicPlayer.Show();
        }

        private void SortButton_Click(object sender, EventArgs e)
        { 
            //SortEntries();
        }

        public class VideoData
        {
            public string YtId { get; set; }
            public bool IsApproved { get; set; }
        }

        // FetchData: ported to WPF
        private async Task FetchData()
        {
            if (currently_fetching) return;
            currently_fetching = true;

            await SendWordFilterGetRequest();
            var newFetchedYtids = new HashSet<string>();
            var newFetchedBLYtids = new HashSet<string>();
            var semaphore = new SemaphoreSlim(ConcurrentTaskNumber);
            bool fetchSuccess = true;

            try
            {
                AppendConsoleText("Fetching Database...");

                var fts = new FetchingService(secID);
                string result = await fts.SendRequest("get-database");

                var jsonArray = JArray.Parse(result);

                var fetchTasks = jsonArray.Select(async entry =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string ytid = entry[0]?.ToString();
                        newFetchedYtids.Add(ytid);

                        string message = entry[1]?.ToString();
                        bool isApproved = entry[2]?.ToObject<bool>() ?? false;
                        double? timestamp = entry[3]?.ToObject<double?>();

                        DateTime timestampUtc = DateTime.MinValue;
                        if (timestamp.HasValue) timestampUtc = DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value).UtcDateTime;

                        if (fetchedYtids.Contains(ytid))
                        {
                            if (fetchedVideoData.TryGetValue(ytid, out var vd) && vd.IsApproved != isApproved)
                            {
                                await UpdateVideoPanelStatusAsync(ytid, isApproved);
                            }
                            if (fetchedVideoData.ContainsKey(ytid)) fetchedVideoData[ytid].IsApproved = isApproved;
                        }
                        else
                        {
                            string videoUrl = $"https://www.youtube.com/watch?v={ytid}";
                            await AddVideoPanelAsync(videoUrl, timestampUtc, message, isApproved);
                            fetchedYtids.Add(ytid);
                            fetchedVideoData[ytid] = new VideoData { YtId = ytid, IsApproved = isApproved };
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(fetchTasks);
                AppendConsoleText("Fetched Database successfully!", Brushes.LightGreen);
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error while fetching database: " + ex.Message, Brushes.OrangeRed);
                fetchSuccess = false;
                UpdateStatus("Offline", new SolidColorBrush(Color.FromRgb(244, 87, 87)));

                // Check for 401 Unauthorized
                if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
                {
                    ShowAuthenticationPrompt();
                }
            }

            try
            {
                AppendConsoleText("Fetching Blacklist...");
                var fts = new FetchingService(secID);
                string result = await fts.SendRequest("get-blacklist");
                var jsonArray = JArray.Parse(result);

                var fetchBlacklistTasks = jsonArray.Select(async entry =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string ytid = entry.ToString();
                        newFetchedBLYtids.Add(ytid);
                        if (fetchedBlacklist.Contains(ytid)) return;
                        // Pass the raw video id into the helper so it can consistently set tags and build the URL
                        await AddBlacklistedVideoPanel(ytid);
                        fetchedBlacklist.Add(ytid);
                    }
                    finally { semaphore.Release(); }
                }).ToList();

                await Task.WhenAll(fetchBlacklistTasks);
                AppendConsoleText("Fetched Blacklist successfully!", Brushes.LightGreen);
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error while fetching Blacklist: " + ex.Message, Brushes.OrangeRed);
                fetchSuccess = false;
                UpdateStatus("Offline", new SolidColorBrush(Color.FromRgb(244, 87, 87)));

                // Check for 401 Unauthorized
                if (ex.Message.Contains("401") || ex.Message.Contains("Unauthorized"))
                {
                    ShowAuthenticationPrompt();
                }
            }

            // Update status to Online if all fetches succeeded
            if (fetchSuccess)
            {
                UpdateStatus("Online", new SolidColorBrush(Color.FromRgb(76, 175, 80)));
            }

            // Remove panels not present anymore
            RemoveNonExistingVideoPanels(newFetchedYtids, newFetchedBLYtids);

            currently_fetching = false;
        }

        private void RemoveNonExistingVideoPanels(HashSet<string> newFetchedYtids, HashSet<string> newFetchedBLYtids)
        {
            // VideoListPanel contains Borders with Tag = ytid
            var toRemove = new List<UIElement>();
            foreach (var child in VideoListPanel.Children.OfType<Border>())
            {
                string ytid = child.Tag?.ToString();
                if (!newFetchedYtids.Contains(ytid))
                {
                    AppendConsoleText("Removed Panel with ytid (remote) " + ytid);
                    toRemove.Add(child);
                    fetchedYtids.Remove(ytid);
                    fetchedVideoData.Remove(ytid);
                }
            }
            foreach (var el in toRemove) VideoListPanel.Children.Remove(el);

            var toRemoveB = new List<UIElement>();
            foreach (var child in VideoBlackListPanel.Children.OfType<Border>())
            {
                string ytid = child.Tag?.ToString();
                if (!newFetchedBLYtids.Contains(ytid))
                {
                    AppendConsoleText("Removed Panel with Blacklisted ytid (remote) " + ytid);
                    toRemoveB.Add(child);
                    fetchedBlacklist.Remove(ytid);
                }
            }
            foreach (var el in toRemoveB) VideoBlackListPanel.Children.Remove(el);
        }

        private async Task UpdateVideoPanelStatusAsync(string videoId, bool isApproved)
        {
            // Find panel by Tag
            var panel = VideoListPanel.Children.OfType<Border>().FirstOrDefault(b => b.Tag?.ToString() == videoId);
            if (panel == null) return;

            // Find status TextBlock inside panel
            TextBlock statusTb = null;
            void Walk(DependencyObject node)
            {
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is TextBlock tb && tb.Padding.Left == 8 && tb.HorizontalAlignment == HorizontalAlignment.Right)
                    {
                        statusTb = tb; return;
                    }
                    Walk(child);
                    if (statusTb != null) return;
                }
            }
            Walk(panel);

            if (statusTb == null) return;

            string mp3FilePath = System.IO.Path.Combine(downloadPath, $"{videoId}.mp3");
            bool isAlreadyDownloaded = File.Exists(mp3FilePath);

            if (isApproved)
            {
                statusTb.Text = "Approved";
                statusTb.Background = Brushes.DarkGreen;
            }
            else
            {
                statusTb.Text = isAlreadyDownloaded ? "Downloaded" : "Added";
                statusTb.Background = isAlreadyDownloaded ? Brushes.DarkBlue : Brushes.Gold;
            }

            AppendConsoleText($"Updated status for video {videoId} to {statusTb.Text}");
        }

        // AddVideoPanelAsync: WPF version of the old WinForms dynamic panel creation + download flow
        public async Task AddVideoPanelAsync(string videoUrl, DateTime? timestamp = null, string message = null, bool? isApproved = null)
        {
            // Create container Border similar to rounded panel
            var videoPanel = new Border
            {
                Width = Double.NaN,
                Height = Double.NaN, // allow auto-sizing so wrapped rows are visible
                MinHeight = 150,     // maintain a minimum height similar to previous fixed height
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("PanelBackground"),
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Tag = YoutubeService.ExtractVideoId(videoUrl)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(231) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); // slightly larger to accommodate buttons

            // Thumbnail
            var thumbnail = new Image { Width = 231, Height = 130, Stretch = System.Windows.Media.Stretch.UniformToFill };
            Grid.SetColumn(thumbnail, 0);
            grid.Children.Add(thumbnail);

            // Center stack for title/length/creator
            var centerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(centerStack, 1);

            var titleText = new TextBlock { Text = "Fetching YouTube title...", FontFamily = new FontFamily("Segoe UI Black"), FontSize = 14, Foreground = Brushes.White };
            centerStack.Children.Add(titleText);

            var lengthText = new TextBlock { Text = "Fetching length...", FontStyle = FontStyles.Italic, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
            centerStack.Children.Add(lengthText);

            var creatorText = new TextBlock { Text = "Fetching YouTube creator...", Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
            centerStack.Children.Add(creatorText);

            // timestamp / message
            var bottomStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };
            var timestampText = new TextBlock { Text = timestamp.HasValue ? timestamp.Value.ToString() : "No Timestamp", Foreground = Brushes.White, Visibility = timestamp.HasValue ? Visibility.Visible : Visibility.Collapsed };
            bottomStack.Children.Add(timestampText);

            var messageText = new TextBlock { Text = string.IsNullOrEmpty(message) ? "" : message, Foreground = Brushes.White, Background = Brushes.DarkOrange, Margin = new Thickness(0, 8, 0, 0) };
            if (string.IsNullOrEmpty(message) || message == "[User] sent in by user") messageText.Visibility = Visibility.Collapsed;
            else bottomStack.Children.Add(messageText); // ensure messageText is actually added to the UI
            centerStack.Children.Add(bottomStack);

            grid.Children.Add(centerStack);

            // Right side: buttons and status
            var rightStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(rightStack, 2);

            var buttonPanel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ItemWidth = 100, ItemHeight = Double.NaN };
            var playButton = new Button { Content = "Queue", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("SuccessButton") };
            var approveButton = new Button { Content = isApproved.HasValue && isApproved.Value ? "Unapprove" : "Approve", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("AccentButton") };
            var blacklistButton = new Button { Content = "Blacklist", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("SecondaryButton")};
            var deleteButton = new Button { Content = "Delete", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("DangerButton")};
            var inspectButton = new Button { Content = "Inspect", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("SecondaryButton")};
            var playNextButton = new Button { Content = "Play Next", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("AccentButton")};
            buttonPanel.Children.Add(playButton);
            buttonPanel.Children.Add(approveButton);
            buttonPanel.Children.Add(blacklistButton);
            buttonPanel.Children.Add(deleteButton);
            buttonPanel.Children.Add(inspectButton);
            buttonPanel.Children.Add(playNextButton);

            rightStack.Children.Add(buttonPanel);

            var statusText = new TextBlock { Text = isApproved.HasValue && isApproved.Value ? "Approved" : "Added", FontWeight = FontWeights.Bold, Background = isApproved.HasValue && isApproved.Value ? Brushes.DarkGreen : Brushes.Gold, Foreground = Brushes.Black, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            rightStack.Children.Add(statusText);

            grid.Children.Add(rightStack);

            videoPanel.Child = grid;

            // Add to the stack panel
            VideoListPanel.Children.Insert(0, videoPanel);

            // Force layout
            await Task.Yield();

            string downloadedFilePath = downloadPath;
            string titleG = string.Empty;
            string creatorG = string.Empty;
            string lengthG = string.Empty;

            try
            {
                string videoId = _youTubeService.GetYouTubeVideoId(videoUrl);
                string mp3FilePath = System.IO.Path.Combine(downloadPath, $"{videoId}.mp3");

                if (File.Exists(mp3FilePath))
                {
                    statusText.Text = "Downloaded";
                    statusText.Background = Brushes.DarkBlue;
                    statusText.Foreground = Brushes.White;
                    EnableControls(true, playButton, approveButton, blacklistButton, deleteButton, inspectButton, playNextButton);

                    downloadedFilePath = mp3FilePath;

                    var (title, length, creator) = await _youTubeService.GetVideoMetadataAsync(videoUrl);
                    UpdateLabels(title, length, creator, titleText, lengthText, creatorText);
                    titleG = title;
                    creatorG = creator;
                    lengthG = length.ToString(@"hh\:mm\:ss");

                    string thumbnailUrl = await _youTubeService.GetThumbnailUrlAsync(videoUrl);
                    var bmp = await LoadBitmapImageFromUrl(thumbnailUrl);
                    if (bmp != null) thumbnail.Source = bmp;
                }
                else
                {
                    EnableControls(false, playButton, approveButton, blacklistButton, deleteButton, inspectButton, playNextButton);
                    statusText.Text = "Downloading";
                    statusText.Background = Brushes.Orange;
                    statusText.Foreground = Brushes.Black;

                    downloadedFilePath = await DownloadVideoAsync(videoUrl);

                    statusText.Text = "Downloaded";
                    statusText.Background = Brushes.DarkBlue;
                    statusText.Foreground = Brushes.White;
                    EnableControls(true, playButton, approveButton, blacklistButton, deleteButton, inspectButton, playNextButton);

                    var (title, length, creator) = await _youTubeService.GetVideoMetadataAsync(videoUrl);
                    UpdateLabels(title, length, creator, titleText, lengthText, creatorText);
                    titleG = title;
                    creatorG = creator;
                    lengthG = length.ToString(@"hh\:mm\:ss");

                    string thumbnailUrl = await _youTubeService.GetThumbnailUrlAsync(videoUrl);
                    var bmp = await LoadBitmapImageFromUrl(thumbnailUrl);
                    if (bmp != null) thumbnail.Source = bmp;
                }

                if (isApproved.HasValue && isApproved.Value)
                {
                    statusText.Text = "Approved";
                    statusText.Background = Brushes.DarkGreen;
                    statusText.Foreground = Brushes.White;
                    approveButton.Content = "Unapprove";
                }

                // approve button
                approveButton.Click += async (s, e) =>
                {
                    try
                    {
                        bool currentlyApproved = statusText.Text == "Approved";
                        if (currentlyApproved)
                        {
                            statusText.Text = "Downloaded";
                            statusText.Background = Brushes.DarkBlue;
                            statusText.Foreground = Brushes.White;
                            approveButton.Content = "Approve";
                        }
                        else
                        {
                            statusText.Text = "Approved";
                            statusText.Background = Brushes.DarkGreen;
                            statusText.Foreground = Brushes.White;
                            approveButton.Content = "Unapprove";
                        }

                        await SendApprovalRequest(videoId, !currentlyApproved);
                        AppendConsoleText($"Approval toggled for {videoId}");
                    }
                    catch (Exception ex)
                    {
                        AppendConsoleText($"Error while approving {videoId}: {ex.Message}");
                    }
                };

                // play button: open music player and load track
                playButton.Click += (s, e) =>
                {
                    try
                    {
                        _musicPlayer.Show();
                        _musicPlayer.AddSongExternal(new Song(titleG, creatorG, thumbnail, lengthG, downloadedFilePath));
                    }
                    catch (Exception ex)
                    {
                        AppendConsoleText($"Error adding track to queue: {ex.Message}");
                    }
                };

                // play next
                playNextButton.Click += (s, e) =>
                {
                    try
                    {
                        _musicPlayer.Show();
                        _musicPlayer.AddNextSongExternal(new Song(titleG, creatorG, thumbnail, lengthG, downloadedFilePath));
                    }
                    catch (Exception ex)
                    {
                        AppendConsoleText($"Error adding track to queue slot 1: {ex.Message}");
                    }
                };

                inspectButton.Click += async (s, e) =>
                {
                    try
                    {
                        // Show lyrics in LyricsText if available
                        LyricsSongTitle.Text = titleG;
                        await ShowLyricsAsync(videoUrl, downloadPath, titleG, creatorG.Replace(" - Topic", ""));
                        // Show a lyrics panel if exists
                        var host = this.FindName("RightCardBorder") as UIElement;
                        if (host != null) host.Visibility = Visibility.Visible;
                    }
                    catch (Exception ex)
                    {
                        AppendConsoleText($"Error inspecting track: {ex.Message}");
                    }
                };

                blacklistButton.Click += async (s, e) =>
                {
                    await SendOtherRequest("blacklist", videoId);
                    VideoListPanel.Children.Remove(videoPanel);
                    fetchedYtids.Remove(videoId);
                    AppendConsoleText($"Blacklisted {videoId}");
                };

                deleteButton.Click += async (s, e) =>
                {
                    var dlg = MessageBox.Show($"Are you sure you want to delete this title?\n{titleG} by {creatorG}\n{videoId}", "Administrative Action", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (dlg == MessageBoxResult.Yes)
                    {
                        await SendOtherRequest("delete", videoId);
                        if (downloadedFilePath != null && File.Exists(downloadedFilePath)) File.Delete(downloadedFilePath);
                        VideoListPanel.Children.Remove(videoPanel);
                        fetchedYtids.Remove(videoId);
                        AppendConsoleText($"Deleted {videoId}");
                    }
                };
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error while handling: " + ex.Message, Brushes.OrangeRed);
                // basic error handling
                videoPanel.Background = Brushes.DarkRed;

                // Check for YouTube 403 rate limit
                if (ex.Message.Contains("403") || ex.Message.Contains("Forbidden") || ex.Message.Contains("rate limit"))
                {
                    ShowYouTubeLimitPrompt();
                }
            }

            // end
        }

        // Helper: enable/disable WPF controls
        private void EnableControls(bool enabled, params Control[] controls)
        {
            foreach (var c in controls)
            {
                c.IsEnabled = enabled;
            }
        }

        private void AppendConsoleText(string text, SolidColorBrush color = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (ConsoleTextBox?.Document == null) return;

                var paragraph = new Paragraph(new Run(text))
                {
                    Margin = new Thickness(0),
                    Foreground = color ?? Brushes.White
                };

                ConsoleTextBox.Document.Blocks.Add(paragraph);
                ConsoleTextBox.ScrollToEnd();
            });
        }

        private void UpdateStatus(string status, SolidColorBrush color)
        {
            Dispatcher.Invoke(() =>
            {
                var statusText = FindName("StatusText") as TextBlock;
                var statusIndicator = FindName("StatusIndicator") as Ellipse;

                if (statusText != null)
                {
                    statusText.Text = status;
                    statusText.Foreground = color;
                }

                if (statusIndicator != null)
                {
                    statusIndicator.Fill = color;
                }
            });
        }

        private void ShowAuthenticationPrompt()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var authPrompt = new AuthPrompt
                    {
                        Owner = this
                    };
                    authPrompt.ShowDialog();
                }
                catch (Exception ex)
                {
                    AppendConsoleText($"Failed to show authentication prompt: {ex.Message}", Brushes.OrangeRed);
                }
            });
        }

        private void ShowYouTubeLimitPrompt()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var ytLimitPrompt = new YoutubeLimitPrompt
                    {
                        Owner = this
                    };
                    ytLimitPrompt.ShowDialog();
                }
                catch (Exception ex)
                {
                    AppendConsoleText($"Failed to show YouTube limit prompt: {ex.Message}", Brushes.OrangeRed);
                }
            });
        }

        private async Task<string> DownloadVideoAsync(string videoUrl)
        {
            return await _youTubeService.DownloadAndConvertVideoAsync(videoUrl, downloadPath);
        }

        private async Task<BitmapImage> LoadBitmapImageFromUrl(string url)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                var bytes = await client.GetByteArrayAsync(url);
                var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private async Task SendApprovalRequest(string ytid, bool approve)
        {
            try
            {
                var fts = new FetchingService(secID);
                var action = approve ? "approve" : "unapprove";
                await fts.SendRequest(action, ytid);
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error sending approval request: " + ex.Message);
            }
        }

        private async Task SendOtherRequest(string action, string ytid)
        {
            try
            {
                var fts = new FetchingService(secID);
                await fts.SendRequest(action, ytid);
            }
            catch (Exception ex)
            {
                AppendConsoleText(ex.Message);
            }
        }

        private void UpdateLabels(string title, TimeSpan length, string creator, TextBlock titleLabel, TextBlock lengthLabel, TextBlock creatorLabel)
        {
            titleLabel.Text = string.IsNullOrEmpty(title) ? "Fetching Youtube Title..." : title;
            lengthLabel.Text = length.ToString(@"hh\:mm\:ss");
            creatorLabel.Text = creator;
        }

        private async Task ShowLyricsAsync(string videoUrl, string downloadPath, string title, string artist)
        {
            try
            {
                string lyrics = await _youTubeService.GetLyricsAsync(artist, title, videoUrl);
                LyricsText.Document.Blocks.Clear();
                LyricsText.Document.Blocks.Add(new Paragraph(new Run(lyrics)));
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error fetching lyrics: " + ex.Message);
            }
        }

        private async Task AddBlacklistedVideoPanel(string ytid)
        {
            // ytid is the raw YouTube id (11 characters). Create WPF panel to represent a blacklisted video
            var videoPanel = new Border
            {
                Height = 100,
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("PanelBackground"),
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10),
                Tag = ytid // store the raw id for easy comparison later
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            var leftStack = new StackPanel { Orientation = Orientation.Vertical };
            var titleText = new TextBlock { Text = "Blacklisted Video", FontFamily = new FontFamily("Segoe UI Black"), FontSize = 14, Foreground = Brushes.Red };
            var creatorText = new TextBlock { Text = "Creator", Foreground = Brushes.White, Margin = new Thickness(0,6,0,0) };
            var lengthText = new TextBlock { Text = "00:00", Foreground = Brushes.White, Margin = new Thickness(0,6,0,0) };
            leftStack.Children.Add(titleText);
            leftStack.Children.Add(creatorText);
            leftStack.Children.Add(lengthText);
            Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            var rightStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
            var unblacklistButton = new Button { Content = "Unblacklist", Width = 80, Margin = new Thickness(2), Style = (Style)FindResource("DangerButton") };
            rightStack.Children.Add(unblacklistButton);
            Grid.SetColumn(rightStack, 1);
            grid.Children.Add(rightStack);

            videoPanel.Child = grid;

            // add to WPF blacklist container
            VideoBlackListPanel.Children.Add(videoPanel);

            // Wire up unblacklist
            unblacklistButton.Click += async (s, e) =>
            {
                try
                {
                    // ytid is already the raw id
                    await SendOtherRequest("unblacklist", ytid);
                    VideoBlackListPanel.Children.Remove(videoPanel);
                    fetchedBlacklist.Remove(ytid);

                    // Try to call FetchData if available
                    var mi = this.GetType().GetMethod("FetchData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (mi != null)
                    {
                        var task = mi.Invoke(this, null) as System.Threading.Tasks.Task;
                        if (task != null) await task;
                    }

                    // Try to update a server status textblock if present
                    var statusTb = this.FindName("ServerStatusText") as TextBlock;
                    if (statusTb != null)
                    {
                        statusTb.Text = "Unblacklisted " + ytid;
                        statusTb.Foreground = Brushes.DeepPink;
                    }
                    else
                    {
                        AppendConsoleText("Unblacklisted " + ytid);
                    }
                }
                catch (Exception ex)
                {
                    var statusTb = this.FindName("ServerStatusText") as TextBlock;
                    if (statusTb != null)
                    {
                        statusTb.Text = "Error while unblacklisting " + ytid + ": " + ex.Message;
                        statusTb.Foreground = Brushes.Red;
                    }
                    else
                    {
                        AppendConsoleText("Error while unblacklisting " + ytid + ": " + ex.Message);
                    }
                }
            };

            // Try to populate metadata
            try
            {
                string videoUrl = $"https://www.youtube.com/watch?v={ytid}";
                var (title, length, creator) = await _youTubeService.GetVideoMetadataAsync(videoUrl);
                titleText.Text = string.IsNullOrEmpty(title) ? "Blacklisted Video" : title;
                creatorText.Text = creator ?? string.Empty;
                lengthText.Text = length.ToString();
            }
            catch (Exception ex)
            {
                AppendConsoleText("Error getting video metadata: " + ex.Message);
            }

            // Try to call ResizeVideoBlackListPanel if exists (compat)
            //var resizeMi = this.GetType().GetMethod("ResizeVideoBlackListPanel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            //if (resizeMi != null)
            //{
            //    try { resizeMi.Invoke(this, null); } catch { }
            //}
        }

        private async Task SendWordFilterRequest(string action, string word)
        {
            try
            {
                var fts = new FetchingService(secID);
                await fts.SendRequest(action, word);

                // Update ConsoleTextBox color on UI thread and append status
                Dispatcher.Invoke(() => ConsoleTextBox.Foreground = Brushes.Lime);
                AppendConsoleText($"Filter Action: {action} with word {word} executed successfully");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ConsoleTextBox.Foreground = Brushes.Red);
                AppendConsoleText(ex.Message);
            }
        }

        private async Task SendWordFilterGetRequest()
        {
            try
            {
                var fts = new FetchingService(secID);

                string response = await fts.SendRequest("get-bad-words");
                wordsToCheck = JsonConvert.DeserializeObject<List<string>>(response);

                Dispatcher.Invoke(() => ConsoleTextBox.Foreground = Brushes.Lime);
                AppendConsoleText("Bad Words were retrieved successfully!");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ConsoleTextBox.Foreground = Brushes.Red);
                AppendConsoleText(ex.Message);
            }
        }

        private void CheckAndHighlightWords()
        {
            int count = HighlightWords(LyricsText, wordsToCheck);
        }

        public int HighlightWords(RichTextBox richTextBox, List<string> words)
        {
            if (richTextBox == null) return 0;

            // Ensure execution on UI thread
            if (!richTextBox.Dispatcher.CheckAccess())
            {
                return (int)richTextBox.Dispatcher.Invoke(() => HighlightWords(richTextBox, words));
            }

            int wordsFound = 0;

            // Safeguard words list
            if (words == null) words = new List<string>();

            // Save original selection
            TextPointer originalSelectionStart = richTextBox.Selection?.Start;
            TextPointer originalSelectionEnd = richTextBox.Selection?.End;

            // Helper to get a TextPointer at a given character offset from Document.ContentStart
            TextPointer GetTextPointerAtOffset(TextPointer start, int charOffset)
            {
                var pointer = start;
                int remaining = charOffset;

                while (pointer != null)
                {
                    if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                    {
                        string run = pointer.GetTextInRun(LogicalDirection.Forward);
                        if (run.Length >= remaining)
                        {
                            // GetPositionAtOffset counts characters within the current run when used on a text pointer located at the start of that run.
                            return pointer.GetPositionAtOffset(remaining, LogicalDirection.Forward);
                        }
                        else
                        {
                            remaining -= run.Length;
                            pointer = pointer.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                        }
                    }
                    else
                    {
                        pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
                    }
                }

                // If we couldn't advance to the requested offset, return DocumentEnd
                return richTextBox.Document.ContentEnd;
            }

            // Clear previous formatting: set background to Transparent (or Black) and foreground to White as legacy behavior
            var fullRange = new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd);
            fullRange.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Black);
            fullRange.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);

            // Get plain text and lowercase for searching
            string fullText = fullRange.Text ?? string.Empty;
            string lowerText = fullText.ToLowerInvariant();

            // Prepare lowercase word list
            var lowerWords = words.Select(w => (w ?? string.Empty).ToLowerInvariant()).Where(s => s.Length > 0).ToList();

            foreach (var word in lowerWords)
            {
                int startIndex = 0;
                while (startIndex < lowerText.Length)
                {
                    int foundIndex = lowerText.IndexOf(word, startIndex, StringComparison.Ordinal);
                    if (foundIndex == -1) break;

                    // Map foundIndex to TextPointers and apply formatting
                    var startPointer = GetTextPointerAtOffset(richTextBox.Document.ContentStart, foundIndex);
                    var endPointer = GetTextPointerAtOffset(richTextBox.Document.ContentStart, foundIndex + word.Length);

                    if (startPointer != null && endPointer != null)
                    {
                        var tr = new TextRange(startPointer, endPointer);
                        tr.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Red);
                        tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.White);
                        wordsFound++;
                    }

                    // Move past this occurrence for next search
                    startIndex = foundIndex + word.Length;
                }
            }

            // Restore selection if available
            if (originalSelectionStart != null && originalSelectionEnd != null)
            {
                try
                {
                    richTextBox.Selection.Select(originalSelectionStart, originalSelectionEnd);
                }
                catch
                {
                    // ignore if restoring fails
                }
            }

            

            // Update status button 'roundedButton1' (assumed to be a WPF Button in XAML)
            try
            {
                var btn = ProblemsLabel;
                if (btn != null)
                {
                    if (wordsFound == 0)
                    {
                        btn.Background = Brushes.Green;
                    }
                    else if (wordsFound <= 5)
                    {
                        btn.Background = Brushes.DarkOrange;
                    }
                    else
                    {
                        btn.Background = Brushes.Red;
                    }

                    btn.Text = $"{wordsFound} problems found";
                }
            }
            catch
            {
                // ignore UI update errors
            }

            return wordsFound;
        }

        private void LyricsRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            SendWordFilterGetRequest();
            CheckAndHighlightWords();
        }

        private async void LyricsBlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (LyricsText.Selection.Text != "")
            {
                await SendWordFilterRequest("add-bad-word", LyricsText.Selection.Text.ToLower());
                await SendWordFilterGetRequest();
                CheckAndHighlightWords();
            }
        }

        private async void LyricsUnblockButton_Click(object sender, RoutedEventArgs e)
        {
            if (LyricsText.Selection.Text != "")
            {
                await SendWordFilterRequest("delete-bad-word", LyricsText.Selection.Text.ToLower());
                await SendWordFilterGetRequest();
                CheckAndHighlightWords();
            }
        }

        private void LyricsLookupButton_Click(object sender, RoutedEventArgs e)
        {
            _youTubeService.SearchLyricsOnDuckDuckGo(LyricsSongTitle.Text, "");
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (closing == false)
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        private void LogoImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            About about = new About();
            about.Show();
        }

        private void TextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            About about = new About();
            about.Show();
        }

        private void LyricsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            RightCardBorder.Visibility = Visibility.Collapsed;
        }
    }
}
