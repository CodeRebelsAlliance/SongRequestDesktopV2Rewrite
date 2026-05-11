using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using System.Diagnostics;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class Settings : Window
    {
        private bool _isTokenVisible;
        private bool _isSyncingTokenControls;

        public Settings()
        {
            InitializeComponent();
            LoadCurrentConfig();
        }

        private T GetControl<T>(string name) where T : class
        {
            return FindName(name) as T;
        }

        private void SetTokenValue(string token)
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            _isSyncingTokenControls = true;
            if (tokenPasswordBox != null) tokenPasswordBox.Password = token ?? string.Empty;
            if (tokenRevealBox != null) tokenRevealBox.Text = token ?? string.Empty;
            _isSyncingTokenControls = false;
        }

        private string GetTokenValue()
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");

            if (_isTokenVisible)
            {
                return tokenRevealBox?.Text ?? tokenPasswordBox?.Password ?? string.Empty;
            }

            return tokenPasswordBox?.Password ?? tokenRevealBox?.Text ?? string.Empty;
        }

        private void UpdateTokenVisibility()
        {
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            var toggleButton = GetControl<Button>("ToggleTokenVisibilityButton");

            if (tokenPasswordBox != null)
            {
                tokenPasswordBox.Visibility = _isTokenVisible ? Visibility.Collapsed : Visibility.Visible;
            }

            if (tokenRevealBox != null)
            {
                tokenRevealBox.Visibility = _isTokenVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (toggleButton != null)
            {
                toggleButton.Content = _isTokenVisible ? "🙈" : "👁";
            }
        }

        private void LoadCurrentConfig()
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return;

            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var requestUrlBox = GetControl<TextBox>("RequestUrlBox");
            var fullscreenCheckBox = GetControl<CheckBox>("PresentationFullscreenCheckBox");
            var normalizeVolumeCheckBox = GetControl<CheckBox>("NormalizeVolumeCheckBox");
            var autoEnqueueCheckBox = GetControl<CheckBox>("AutoEnqueueCheckBox");
            var captionFallbackCheckBox = GetControl<CheckBox>("CaptionFallbackCheckBox");
            var enableAnnouncementsCheckBox = GetControl<CheckBox>("EnableAnnouncementsCheckBox");

            if (fetchingBox != null) fetchingBox.Text = cfg.FetchingTimer.ToString();
            if (threadsBox != null) threadsBox.Text = cfg.Threads.ToString();
            SetTokenValue(cfg.BearerToken);
            if (addressBox != null) addressBox.Text = cfg.Address;
            if (sortingBox != null) sortingBox.Text = cfg.DefaultSorting;
            if (requestUrlBox != null) requestUrlBox.Text = cfg.RequestUrl;
            if (fullscreenCheckBox != null) fullscreenCheckBox.IsChecked = cfg.PresentationFullscreen;
            if (normalizeVolumeCheckBox != null) normalizeVolumeCheckBox.IsChecked = cfg.NormalizeVolume;
            if (autoEnqueueCheckBox != null) autoEnqueueCheckBox.IsChecked = cfg.AutoEnqueue;
            if (captionFallbackCheckBox != null) captionFallbackCheckBox.IsChecked = cfg.UseCaptionLyricsFallback;
            if (enableAnnouncementsCheckBox != null) enableAnnouncementsCheckBox.IsChecked = cfg.EnableAnnouncements;

            _isTokenVisible = false;
            UpdateTokenVisibility();

            // Fetch server-side sendin allowed status
            _ = FetchSendinAllowedStatusAsync();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var requestUrlBox = GetControl<TextBox>("RequestUrlBox");
            var fullscreenCheckBox = GetControl<CheckBox>("PresentationFullscreenCheckBox");
            var normalizeVolumeCheckBox = GetControl<CheckBox>("NormalizeVolumeCheckBox");
            var autoEnqueueCheckBox = GetControl<CheckBox>("AutoEnqueueCheckBox");
            var captionFallbackCheckBox = GetControl<CheckBox>("CaptionFallbackCheckBox");
            var enableAnnouncementsCheckBox = GetControl<CheckBox>("EnableAnnouncementsCheckBox");
            var statusTb = GetControl<TextBlock>("StatusText");
            var tokenValue = GetTokenValue();

            try
            {
                int fetching = Math.Max(1, int.Parse(fetchingBox?.Text ?? "1"));
                int threads = Math.Max(1, int.Parse(threadsBox?.Text ?? "1"));

                ConfigService.Instance.Update(cfg =>
                {
                    cfg.FetchingTimer = fetching;
                    cfg.Threads = threads;
                    cfg.BearerToken = tokenValue;
                    cfg.Address = addressBox?.Text ?? string.Empty;
                    cfg.DefaultSorting = sortingBox?.Text ?? string.Empty;
                    cfg.RequestUrl = requestUrlBox?.Text ?? "https://example.com/request";
                    cfg.PresentationFullscreen = fullscreenCheckBox?.IsChecked ?? false;
                    cfg.NormalizeVolume = normalizeVolumeCheckBox?.IsChecked ?? false;
                    cfg.AutoEnqueue = autoEnqueueCheckBox?.IsChecked ?? false;
                    cfg.UseCaptionLyricsFallback = captionFallbackCheckBox?.IsChecked ?? true;
                    cfg.EnableAnnouncements = enableAnnouncementsCheckBox?.IsChecked ?? true;

                    // If normalization is turned off, deactivate it
                    if (!cfg.NormalizeVolume)
                    {
                        cfg.NormalizationActive = false;
                    }
                });

                if (statusTb != null)
                {
                    statusTb.Text = "Saved.";
                    statusTb.Foreground = Brushes.LightGreen;
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Error while saving: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private void ToggleTokenVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            _isTokenVisible = !_isTokenVisible;
            UpdateTokenVisibility();
        }

        private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingTokenControls) return;
            var tokenPasswordBox = sender as PasswordBox;
            var tokenRevealBox = GetControl<TextBox>("TokenRevealBox");
            if (tokenPasswordBox == null || tokenRevealBox == null) return;

            _isSyncingTokenControls = true;
            tokenRevealBox.Text = tokenPasswordBox.Password;
            _isSyncingTokenControls = false;
        }

        private void TokenRevealBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingTokenControls) return;
            var tokenRevealBox = sender as TextBox;
            var tokenPasswordBox = GetControl<PasswordBox>("TokenPasswordBox");
            if (tokenRevealBox == null || tokenPasswordBox == null) return;

            _isSyncingTokenControls = true;
            tokenPasswordBox.Password = tokenRevealBox.Text ?? string.Empty;
            _isSyncingTokenControls = false;
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Instance.Reload();
            LoadCurrentConfig();
            var statusTb = GetControl<TextBlock>("StatusText");
            if (statusTb != null)
            {
                statusTb.Text = "Reloaded from disk.";
                statusTb.Foreground = Brushes.LightGray;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            // Find the main YoutubeForm window and clear its console
            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        rtb.Document.Blocks.Clear();
                        if (statusTb != null)
                        {
                            statusTb.Text = "Console cleared.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to clear.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void AppendConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            var appendBox = GetControl<TextBox>("AppendTextBox");
            var statusTb = GetControl<TextBlock>("StatusText");
            string text = appendBox?.Text ?? string.Empty;

            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        var paragraph = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text))
                        {
                            Margin = new Thickness(0),
                            Foreground = Brushes.White
                        };
                        rtb.Document.Blocks.Add(paragraph);
                        rtb.ScrollToEnd();
                        if (statusTb != null)
                        {
                            statusTb.Text = "Appended to console.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to append to.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void ApplyColorButton_Click(object sender, RoutedEventArgs e)
        {
            var combo = GetControl<ComboBox>("ConsoleColorCombo");
            var statusTb = GetControl<TextBlock>("StatusText");
            var sel = (combo?.SelectedItem as ComboBoxItem)?.Content as string;
            Brush brush = Brushes.White;
            switch (sel)
            {
                case "Green": brush = Brushes.Lime; break;
                case "Red": brush = Brushes.Red; break;
                case "White": brush = Brushes.White; break;
                default: brush = Brushes.White; break;
            }

            foreach (Window w in Application.Current.Windows)
            {
                if (w is YoutubeForm yf)
                {
                    var rtb = yf.FindName("ConsoleTextBox") as System.Windows.Controls.RichTextBox;
                    if (rtb != null && rtb.Document != null)
                    {
                        // Apply color to all existing paragraphs
                        foreach (var block in rtb.Document.Blocks)
                        {
                            if (block is System.Windows.Documents.Paragraph para)
                            {
                                para.Foreground = brush;
                            }
                        }
                        if (statusTb != null)
                        {
                            statusTb.Text = "Console color applied.";
                            statusTb.Foreground = Brushes.LightGray;
                        }
                        return;
                    }
                }
            }

            if (statusTb != null)
            {
                statusTb.Text = "Could not find console to set color.";
                statusTb.Foreground = Brushes.OrangeRed;
            }
        }

        private void RegisterAuth_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var address = ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000";
                var url = $"https://schuelerapp.by-cra.net/sign-jwt?audience={Uri.EscapeDataString(address)}";
                var psi = new ProcessStartInfo { FileName = url, UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                var statusTb = GetControl<TextBlock>("StatusText");
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to open browser: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private async System.Threading.Tasks.Task FetchSendinAllowedStatusAsync()
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            var sendinCheckBox = GetControl<CheckBox>("SendinAllowedCheckBox");

            try
            {
                var cfg = ConfigService.Instance.Current;
                if (cfg == null) return;

                var fts = new FetchingService(cfg.BearerToken);
                var result = await fts.SendRequest("get-sendin-allowed");

                // Parse the result - it's a list with a single boolean
                var jsonArray = Newtonsoft.Json.Linq.JArray.Parse(result);
                if (jsonArray.Count > 0)
                {
                    bool sendinAllowed = jsonArray[0].ToObject<bool>();
                    if (sendinCheckBox != null)
                    {
                        sendinCheckBox.IsChecked = sendinAllowed;
                    }

                    if (statusTb != null)
                    {
                        statusTb.Text = $"Server status: Sending {(sendinAllowed ? "Allowed" : "Disabled")}";
                        statusTb.Foreground = sendinAllowed ? Brushes.LightGreen : Brushes.OrangeRed;
                    }
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to fetch sendin status: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
        }

        private async void ToggleSendinButton_Click(object sender, RoutedEventArgs e)
        {
            var statusTb = GetControl<TextBlock>("StatusText");
            var toggleButton = GetControl<Button>("ToggleSendinButton");

            try
            {
                if (toggleButton != null) toggleButton.IsEnabled = false;

                var cfg = ConfigService.Instance.Current;
                if (cfg == null) return;

                var fts = new FetchingService(cfg.BearerToken);
                var result = await fts.SendRequest("toggle");

                // Refresh the status after toggle
                await FetchSendinAllowedStatusAsync();

                if (statusTb != null)
                {
                    statusTb.Text = "Toggled sendin status successfully";
                    statusTb.Foreground = Brushes.LightGreen;
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null)
                {
                    statusTb.Text = "Failed to toggle sendin status: " + ex.Message;
                    statusTb.Foreground = Brushes.OrangeRed;
                }
            }
            finally
            {
                if (toggleButton != null) toggleButton.IsEnabled = true;
            }
        }
    }
}
