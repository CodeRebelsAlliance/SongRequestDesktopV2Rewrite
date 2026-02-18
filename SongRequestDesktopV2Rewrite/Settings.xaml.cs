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
        public Settings()
        {
            InitializeComponent();
            LoadCurrentConfig();
        }

        private T GetControl<T>(string name) where T : class
        {
            return FindName(name) as T;
        }

        private void LoadCurrentConfig()
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return;

            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var tokenBox = GetControl<TextBox>("TokenBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var loudnessCheck = GetControl<System.Windows.Controls.CheckBox>("LoudnessNormCheckBox");
            var loudnessSlider = GetControl<Slider>("LoudnessTargetSlider");
            var loudnessLabel = GetControl<TextBlock>("LoudnessTargetLabel");

            if (fetchingBox != null) fetchingBox.Text = cfg.FetchingTimer.ToString();
            if (threadsBox != null) threadsBox.Text = cfg.Threads.ToString();
            if (tokenBox != null) tokenBox.Text = cfg.BearerToken;
            if (addressBox != null) addressBox.Text = cfg.Address;
            if (sortingBox != null) sortingBox.Text = cfg.DefaultSorting;
            if (loudnessCheck != null) loudnessCheck.IsChecked = cfg.LoudnessNormalization;
            if (loudnessSlider != null)
            {
                loudnessSlider.Value = cfg.LoudnessTarget;
                loudnessSlider.ValueChanged += (s, e) =>
                {
                    if (loudnessLabel != null) loudnessLabel.Text = loudnessSlider.Value.ToString("F1");
                };
            }
            if (loudnessLabel != null) loudnessLabel.Text = cfg.LoudnessTarget.ToString("F1");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var fetchingBox = GetControl<TextBox>("FetchingTimerBox");
            var threadsBox = GetControl<TextBox>("ThreadsBox");
            var tokenBox = GetControl<TextBox>("TokenBox");
            var addressBox = GetControl<TextBox>("AddressBox");
            var sortingBox = GetControl<TextBox>("DefaultSortingBox");
            var loudnessCheck = GetControl<System.Windows.Controls.CheckBox>("LoudnessNormCheckBox");
            var loudnessSlider = GetControl<Slider>("LoudnessTargetSlider");
            var statusTb = GetControl<TextBlock>("StatusText");

            try
            {
                int fetching = Math.Max(1, int.Parse(fetchingBox?.Text ?? "1"));
                int threads = Math.Max(1, int.Parse(threadsBox?.Text ?? "1"));

                ConfigService.Instance.Update(cfg =>
                {
                    cfg.FetchingTimer = fetching;
                    cfg.Threads = threads;
                    cfg.BearerToken = tokenBox?.Text ?? string.Empty;
                    cfg.Address = addressBox?.Text ?? string.Empty;
                    cfg.DefaultSorting = sortingBox?.Text ?? string.Empty;
                    cfg.LoudnessNormalization = loudnessCheck?.IsChecked ?? false;
                    cfg.LoudnessTarget = loudnessSlider?.Value ?? -14.0;
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
                    var tb = yf.FindName("ConsoleTextBox") as TextBox;
                    if (tb != null)
                    {
                        tb.Clear();
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
                    var tb = yf.FindName("ConsoleTextBox") as TextBox;
                    if (tb != null)
                    {
                        tb.AppendText(text + "\n");
                        tb.ScrollToEnd();
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
                    var tb = yf.FindName("ConsoleTextBox") as TextBox;
                    if (tb != null)
                    {
                        tb.Foreground = brush;
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
                var url = $"https://redstefan.software/schuelerapp/sign-jwt?audience={Uri.EscapeDataString(address)}";
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
    }
}
