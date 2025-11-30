using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace SongRequestDesktopV2Rewrite
{
    public partial class About : Window
    {
        public About()
        {
            InitializeComponent();
            // set version text from assembly (use FindName to avoid generated field mismatch)
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
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
                // Simple check: query GitHub releases latest tag via API
                var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SongRequestApp");
                var url = "https://api.github.com/repos/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases/latest";
                var resp = await client.GetStringAsync(url);
                dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject(resp);
                string latestTag = json.tag_name;
                var vLocal = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
                if (statusTb != null) statusTb.Text = $"Latest: {latestTag} — Local: {vLocal}";
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
