using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool shown = false;
        private DispatcherTimer _progressTimer;
        private double _progress = 0;
        private readonly string[] _statusMessages = new[]
        {
            "Initializing...",
            "Loading configuration...",
            "Checking for updates...",
            "Preparing authentication...",
            "Almost ready..."
        };
        private int _currentStatusIndex = 0;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;

            // Get version from assembly
            var version = About.version;
            VersionText.Text = $"Version {version}";
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartLoadingAnimation();
            LoadingProcess();
        }

        private void StartLoadingAnimation()
        {
            // Animate progress bar
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };

            _progressTimer.Tick += (s, e) =>
            {
                _progress += 0.8; // Adjust speed here

                if (_progress >= 100)
                {
                    _progress = 100;
                    _progressTimer?.Stop();
                }

                // Update progress bar width
                ProgressFill.Width = (ActualWidth - 80) * (_progress / 100);

                // Update status text at intervals
                int newStatusIndex = (int)(_progress / 20); // Change every 20%
                if (newStatusIndex < _statusMessages.Length && newStatusIndex != _currentStatusIndex)
                {
                    _currentStatusIndex = newStatusIndex;
                    StatusText.Text = _statusMessages[_currentStatusIndex];
                }
            };

            _progressTimer.Start();

            // Shimmer animation
            var shimmerAnimation = new DoubleAnimation
            {
                From = -80,
                To = ActualWidth,
                Duration = TimeSpan.FromSeconds(1.5),
                RepeatBehavior = RepeatBehavior.Forever
            };
            ShimmerTransform.BeginAnimation(TranslateTransform.XProperty, shimmerAnimation);
        }

        private async void LoadingProcess()
        {
            // Simulate loading stages
            await Task.Delay(500);  // Initial delay

            // Check for updates before showing Authentication
            await CheckForUpdatesAsync();

            await Task.Delay(1500); // Let progress bar finish

            // Complete the progress
            _progress = 100;
            ProgressFill.Width = ActualWidth - 80;
            StatusText.Text = "Ready!";

            await Task.Delay(300); // Brief pause to show completion

            // Show authentication window
            var authForm = new Authentication();
            authForm.CookiesRetrieved += AuthForm_CookiesRetrieved;
            authForm.Show();
            shown = true;
            Hide();
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                StatusText.Text = "Checking for updates...";
                var updateInfo = await UpdateService.CheckForUpdatesAsync();

                if (updateInfo.UpdateAvailable)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Stop progress timer during update prompt
                        _progressTimer?.Stop();

                        var updatePrompt = new UpdatePrompt(
                            updateInfo.DownloadUrl,
                            updateInfo.LatestVersion,
                            updateInfo.CurrentVersion,
                            updateInfo.ReleaseNotes)
                        {
                            Owner = this
                        };

                        updatePrompt.ShowDialog();

                        // Resume progress if user declined update
                        if (_progressTimer != null && _progress < 100)
                        {
                            _progressTimer.Start();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // Silently fail - don't block app startup if update check fails
                System.Diagnostics.Debug.WriteLine($"Update check error: {ex.Message}");
            }
        }

        private void AuthForm_CookiesRetrieved(IReadOnlyList<System.Net.Cookie> cookies)
        {
            if (shown)
            {
                shown = false;
                var youtubeForm = new YoutubeForm(cookies);
                youtubeForm.Show();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _progressTimer?.Stop();
            base.OnClosed(e);
        }
    }
}