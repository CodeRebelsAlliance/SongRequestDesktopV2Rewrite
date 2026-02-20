using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SongRequestDesktopV2Rewrite
{
    public partial class SubmitSongDialog : Window
    {
        private bool isUrlMode = true;
        private string selectedVideoId = null;
        private readonly YoutubeService _youtubeService;
        private readonly string _secretKey;

        public string SubmittedVideoId { get; private set; }
        public string SubmittedMessage { get; private set; }

        public SubmitSongDialog(YoutubeService youtubeService, string secretKey)
        {
            InitializeComponent();
            _youtubeService = youtubeService;
            _secretKey = secretKey;

            // Set focus to URL textbox when dialog loads
            Loaded += async (s, e) =>
            {
                // Small delay to ensure window is fully rendered
                await Task.Delay(100);
                UrlTextBox.Focus();
                UrlTextBox.SelectAll();
            };
        }

        private void UrlModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isUrlMode)
            {
                isUrlMode = true;
                UrlModePanel.Visibility = Visibility.Visible;
                SearchModePanel.Visibility = Visibility.Collapsed;
                UrlModeButton.Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
                SearchModeButton.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
                SubmitButton.IsEnabled = !string.IsNullOrWhiteSpace(UrlTextBox.Text);

                // Set focus to URL textbox
                UrlTextBox.Focus();
                UrlTextBox.SelectAll();
            }
        }

        private void SearchModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (isUrlMode)
            {
                isUrlMode = false;
                UrlModePanel.Visibility = Visibility.Collapsed;
                SearchModePanel.Visibility = Visibility.Visible;
                UrlModeButton.Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
                SearchModeButton.Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
                SubmitButton.IsEnabled = selectedVideoId != null;

                // Set focus to search textbox
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            }
        }

        private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isUrlMode)
            {
                SubmitButton.IsEnabled = !string.IsNullOrWhiteSpace(UrlTextBox.Text);
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                await PerformSearch(SearchTextBox.Text);
            }
            else
            {
                SearchStatusText.Text = "Please enter a search query";
            }
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                SearchButton_Click(SearchButton, new RoutedEventArgs());
            }
        }

        private async Task PerformSearch(string query)
        {
            try
            {
                SearchStatusText.Text = "Searching...";
                SearchResultsPanel.Children.Clear();

                var results = await _youtubeService.SearchAsync(query, maxResults: 5);

                if (results == null || !results.Any())
                {
                    SearchStatusText.Text = "No results found";
                    SearchResultsScroll.Visibility = Visibility.Collapsed;
                    return;
                }

                SearchStatusText.Text = $"Found {results.Count} results";
                SearchResultsScroll.Visibility = Visibility.Visible;

                foreach (var result in results)
                {
                    var resultButton = new Button
                    {
                        Height = 60,
                        Margin = new Thickness(0, 0, 0, 8),
                        Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                        BorderThickness = new Thickness(2),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(10),
                        Cursor = Cursors.Hand,
                        Tag = result.VideoId
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    // Thumbnail placeholder
                    var thumbnailBorder = new Border
                    {
                        Width = 40,
                        Height = 40,
                        Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                        CornerRadius = new CornerRadius(4),
                        Child = new TextBlock
                        {
                            Text = "🎵",
                            FontSize = 20,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.White
                        }
                    };
                    Grid.SetColumn(thumbnailBorder, 0);
                    grid.Children.Add(thumbnailBorder);

                    // Title and channel
                    var textStack = new StackPanel
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    textStack.Children.Add(new TextBlock
                    {
                        Text = result.Title,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 450
                    });

                    textStack.Children.Add(new TextBlock
                    {
                        Text = result.Author,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 450
                    });

                    Grid.SetColumn(textStack, 1);
                    grid.Children.Add(textStack);

                    resultButton.Content = grid;
                    resultButton.Click += SearchResult_Click;

                    SearchResultsPanel.Children.Add(resultButton);
                }
            }
            catch (Exception ex)
            {
                SearchStatusText.Text = $"Error: {ex.Message}";
                SearchResultsScroll.Visibility = Visibility.Collapsed;
            }
        }

        private void SearchResult_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                selectedVideoId = button.Tag?.ToString();

                // Update all buttons to show selection
                foreach (var child in SearchResultsPanel.Children)
                {
                    if (child is Button btn)
                    {
                        btn.BorderBrush = btn.Tag?.ToString() == selectedVideoId
                            ? new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF))
                            : new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                        btn.BorderThickness = new Thickness(btn.Tag?.ToString() == selectedVideoId ? 3 : 2);
                    }
                }

                SubmitButton.IsEnabled = true;
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SubmitButton.IsEnabled = false;
                SubmitButton.Content = "Submitting...";

                if (isUrlMode)
                {
                    // Extract video ID from URL
                    string url = UrlTextBox.Text.Trim();
                    string videoId = ExtractVideoId(url);

                    if (string.IsNullOrEmpty(videoId))
                    {
                        MessageBox.Show("Invalid YouTube URL or Video ID", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        SubmitButton.Content = "Submit";
                        SubmitButton.IsEnabled = true;
                        return;
                    }

                    SubmittedVideoId = videoId;
                    SubmittedMessage = MessageTextBox.Text.Trim();
                }
                else
                {
                    // Use selected video from search
                    if (string.IsNullOrEmpty(selectedVideoId))
                    {
                        MessageBox.Show("Please select a video from search results", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        SubmitButton.Content = "Submit";
                        SubmitButton.IsEnabled = true;
                        return;
                    }

                    SubmittedVideoId = selectedVideoId;
                    SubmittedMessage = "[Admin] Added via search";
                }

                // Submit to server
                await SubmitToServer(SubmittedVideoId, SubmittedMessage);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error submitting song: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SubmitButton.Content = "Submit";
                SubmitButton.IsEnabled = true;
            }
        }

        private async Task SubmitToServer(string videoId, string message)
        {
            try
            {
                using var httpClient = new HttpClient();
                var baseUrl = ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000";
                var url = $"{baseUrl}/fetch?method=add";

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", _secretKey);

                var content = new MultipartFormDataContent();
                content.Add(new StringContent(videoId), "ytid");

                request.Content = content;
                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to submit to server: {ex.Message}");
            }
        }

        private string ExtractVideoId(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            // If it's already 11 characters, assume it's a video ID
            if (url.Length == 11 && !url.Contains('/') && !url.Contains('.'))
                return url;

            // Clean up URL
            url = url.Replace("https://", "").Replace("http://", "").Replace("www.", "").Replace("music.", "").Replace("m.", "");

            // youtu.be/ format
            if (url.Contains("youtu.be/"))
            {
                int idx = url.IndexOf("youtu.be/") + 9;
                return url.Substring(idx, Math.Min(11, url.Length - idx));
            }

            // ?v= or &v= format
            if (url.Contains("v="))
            {
                int idx = url.IndexOf("v=") + 2;
                return url.Substring(idx, Math.Min(11, url.Length - idx));
            }

            // /watch/ format
            if (url.Contains("/watch/"))
            {
                int idx = url.IndexOf("/watch/") + 7;
                return url.Substring(idx, Math.Min(11, url.Length - idx));
            }

            return null;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && SubmitButton.IsEnabled)
            {
                SubmitButton_Click(SubmitButton, new RoutedEventArgs());
            }
        }
    }
}
