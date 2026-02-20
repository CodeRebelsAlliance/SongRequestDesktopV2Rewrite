using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    public partial class YoutubeLimitPrompt : Window
    {
        public YoutubeLimitPrompt()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void ClearAndRestartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "This will clear your YouTube login credentials and restart the application.\n\n" +
                    "Are you sure you want to continue?",
                    "Confirm Clear & Restart",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // Clear YouTube authentication
                    ClearYouTubeAuth();

                    // Close this dialog
                    this.DialogResult = true;
                    this.Close();

                    // Restart the application
                    RestartApplication();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear login and restart:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearYouTubeAuth()
        {
            try
            {
                // Get the app data folder
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var appFolder = Path.Combine(appData, "SongRequestDesktopV2Rewrite");

                // Delete YouTube authentication files
                var youtubeAuthFile = Path.Combine(appFolder, "youtube_auth.json");
                if (File.Exists(youtubeAuthFile))
                {
                    File.Delete(youtubeAuthFile);
                }

                // Delete browser cache/cookies that might contain YouTube session
                var cachePath = Path.Combine(appFolder, "cache");
                if (Directory.Exists(cachePath))
                {
                    Directory.Delete(cachePath, true);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - restart anyway
                Debug.WriteLine($"Error clearing YouTube auth: {ex.Message}");
            }
        }

        private void RestartApplication()
        {
            try
            {
                // Get the current executable path
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("Could not determine application path for restart.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Start a new instance
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });

                // Shutdown the current application
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restart application:\n{ex.Message}\n\nPlease restart manually.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
