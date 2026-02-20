using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace SongRequestDesktopV2Rewrite
{
    public partial class UpdatePrompt : Window
    {
        private readonly string _downloadUrl;
        private readonly string _newVersion;
        private readonly string _currentVersion;
        private bool _isDownloading = false;

        public UpdatePrompt(string downloadUrl, string newVersion, string currentVersion, string releaseNotes = null)
        {
            InitializeComponent();
            _downloadUrl = downloadUrl;
            _newVersion = newVersion;
            _currentVersion = currentVersion;

            VersionInfo.Text = $"New Version: {newVersion} (Current: {currentVersion})";

            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                ReleaseNotesTextBox.Text = FormatMarkdown(releaseNotes);
            }
            else
            {
                ReleaseNotesTextBox.Text = "No release notes available.";
            }
        }

        private string FormatMarkdown(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return "No release notes available.";

            var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var formatted = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Headers (## -> larger text)
                if (trimmedLine.StartsWith("### "))
                {
                    formatted.AppendLine($"  • {trimmedLine.Substring(4)}");
                }
                else if (trimmedLine.StartsWith("## "))
                {
                    if (formatted.Length > 0)
                        formatted.AppendLine();
                    formatted.AppendLine(trimmedLine.Substring(3).ToUpper());
                }
                else if (trimmedLine.StartsWith("# "))
                {
                    if (formatted.Length > 0)
                        formatted.AppendLine();
                    formatted.AppendLine(trimmedLine.Substring(2).ToUpper());
                    formatted.AppendLine(new string('─', Math.Min(40, trimmedLine.Length - 2)));
                }
                // List items
                else if (trimmedLine.StartsWith("- ") || trimmedLine.StartsWith("* "))
                {
                    formatted.AppendLine($"  • {trimmedLine.Substring(2)}");
                }
                // Bold text (basic support)
                else if (!string.IsNullOrWhiteSpace(trimmedLine))
                {
                    var formattedLine = trimmedLine
                        .Replace("**", "")
                        .Replace("__", "");
                    formatted.AppendLine(formattedLine);
                }
                else
                {
                    formatted.AppendLine();
                }
            }

            return formatted.ToString();
        }

        private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private async void InstallNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;

            try
            {
                _isDownloading = true;
                InstallNowButton.IsEnabled = false;
                RemindLaterButton.IsEnabled = false;

                // Show progress panel
                ProgressPanel.Visibility = Visibility.Visible;

                // Download the update
                var zipPath = await DownloadUpdateAsync();

                // Extract to temp
                ProgressText.Text = "Extracting...";
                DownloadProgress.IsIndeterminate = true;
                var extractPath = Path.Combine(Path.GetTempPath(), "SongRequestUpdate");
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // Create and run updater script
                ProgressText.Text = "Preparing update...";
                await CreateAndRunUpdaterScript(extractPath);

                // Close this dialog
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to download and install update:\n{ex.Message}",
                    "Update Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                InstallNowButton.IsEnabled = true;
                RemindLaterButton.IsEnabled = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
                _isDownloading = false;
            }
        }

        private async Task<string> DownloadUpdateAsync()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "songrequest_update.zip");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);

                using (var response = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    var canReportProgress = totalBytes != -1;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalBytesRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;

                            if (canReportProgress)
                            {
                                var progress = (double)totalBytesRead / totalBytes * 100;
                                Dispatcher.Invoke(() =>
                                {
                                    DownloadProgress.IsIndeterminate = false;
                                    DownloadProgress.Value = progress;
                                    ProgressText.Text = $"Downloading... {progress:F1}%";
                                });
                            }
                        }
                    }
                }
            }

            return tempPath;
        }

        private async Task CreateAndRunUpdaterScript(string extractPath)
        {
            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            var scriptPath = Path.Combine(Path.GetTempPath(), "update_songrequest.bat");
            var exePath = Path.Combine(appPath, "SongRequest V2.exe");

            // Create batch script
            var scriptLines = new[]
            {
                "@echo off",
                "echo SongRequest Updater",
                "echo.",
                "",
                "REM Wait for main application to close",
                "timeout /t 2 /nobreak >nul",
                "",
                "REM Kill any remaining SongRequest processes",
                "taskkill /F /IM 'SongRequest V2.exe' 2>nul",
                "",
                "REM Wait a bit more",
                "timeout /t 1 /nobreak >nul",
                "",
                "echo Updating files...",
                "",
                "REM Copy new files (overwrite existing)",
                $"xcopy \"{extractPath}\\*\" \"{appPath}\" /E /Y /I /Q",
                "",
                "echo Update complete!",
                "echo.",
                "",
                "REM Start the application again",
                $"start \"\" \"{exePath}\"",
                "",
                "REM Clean up",
                "timeout /t 2 /nobreak >nul",
                $"rmdir /S /Q \"{extractPath}\"",
                $"del \"{scriptPath}\""
            };

            var script = string.Join(Environment.NewLine, scriptLines);
            await File.WriteAllTextAsync(scriptPath, script);

            // Start the updater script
            var psi = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);

            // Give script time to start
            await Task.Delay(500);

            // Shutdown the current application
            Application.Current.Shutdown();
        }
    }
}
