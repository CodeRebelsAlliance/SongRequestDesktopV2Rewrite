using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace SongRequestDesktopV2Rewrite
{
    public partial class About : Window
    {
        public static string version = "2.3";
        
        public About()
        {
            InitializeComponent();
            // set version text from assembly (use FindName to avoid generated field mismatch)
            try
            {
                var v = About.version;
                var vt = this.FindName("VersionText") as System.Windows.Controls.TextBlock;
                if (vt != null && v != null) vt.Text = "Version " + v.ToString();
            }
            catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            var statusTb = this.FindName("UpdateStatus") as System.Windows.Controls.TextBlock;
            if (statusTb != null) statusTb.Text = "Checking...";

            try
            {
                var updateInfo = await UpdateService.CheckForUpdatesAsync();

                if (updateInfo.UpdateAvailable)
                {
                    if (statusTb != null) 
                        statusTb.Text = $"Update available: {updateInfo.LatestVersion} (Current: {updateInfo.CurrentVersion})";

                    // Show update prompt
                    var updatePrompt = new UpdatePrompt(
                        updateInfo.DownloadUrl,
                        updateInfo.LatestVersion,
                        updateInfo.CurrentVersion,
                        updateInfo.ReleaseNotes)
                    {
                        Owner = this
                    };
                    updatePrompt.ShowDialog();
                }
                else
                {
                    if (statusTb != null) 
                        statusTb.Text = $"You're up to date! (Version {updateInfo.CurrentVersion})";
                }
            }
            catch (Exception ex)
            {
                if (statusTb != null) statusTb.Text = "Update check failed";
                Debug.WriteLine(ex.ToString());
            }
        }

        private void Badge_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Image img && img.Tag is string url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
        }
    }
}
