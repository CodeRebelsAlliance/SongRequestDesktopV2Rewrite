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

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool shown = false;
        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private async void LoadingProcess()
        {
            await Task.Delay(2000);

            // Check for updates before showing Authentication
            await CheckForUpdatesAsync();

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
                var updateInfo = await UpdateService.CheckForUpdatesAsync();

                if (updateInfo.UpdateAvailable)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var updatePrompt = new UpdatePrompt(
                            updateInfo.DownloadUrl,
                            updateInfo.LatestVersion,
                            updateInfo.CurrentVersion,
                            updateInfo.ReleaseNotes)
                        {
                            Owner = this
                        };

                        updatePrompt.ShowDialog();
                        // If user chose to install, the app will be shut down by the updater
                        // If user chose "Remind Me Later", we continue normally
                    });
                }
            }
            catch (Exception ex)
            {
                // Silently fail - don't block app startup if update check fails
                System.Diagnostics.Debug.WriteLine($"Update check error: {ex.Message}");
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingProcess();
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
    }
}