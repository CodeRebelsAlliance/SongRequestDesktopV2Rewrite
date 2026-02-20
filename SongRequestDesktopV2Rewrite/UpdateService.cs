using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite
{
    public class UpdateService
    {
        private const string GITHUB_REPO = "CodeRebelsAlliance/SongRequestDesktopV2Rewrite";
        private const string GITHUB_API_URL = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";

        public class UpdateInfo
        {
            public bool UpdateAvailable { get; set; }
            public string LatestVersion { get; set; }
            public string CurrentVersion { get; set; }
            public string DownloadUrl { get; set; }
            public string ReleaseNotes { get; set; }
        }

        public static async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SongRequestApp");
                    client.Timeout = TimeSpan.FromSeconds(10);

                    var response = await client.GetStringAsync(GITHUB_API_URL);
                    var json = JObject.Parse(response);

                    var latestTag = json["tag_name"]?.ToString() ?? "";
                    var releaseNotes = json["body"]?.ToString() ?? "";

                    // Find the songrequest.zip asset
                    string downloadUrl = null;
                    var assets = json["assets"] as JArray;
                    if (assets != null)
                    {
                        foreach (var asset in assets)
                        {
                            var name = asset["name"]?.ToString() ?? "";
                            if (name.Equals("songrequest.zip", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset["browser_download_url"]?.ToString();
                                break;
                            }
                        }
                    }

                    var currentVersion = GetCurrentVersion();
                    var updateAvailable = IsNewerVersion(latestTag, currentVersion);

                    // Debug logging
                    System.Diagnostics.Debug.WriteLine($"Update Check - Latest: {latestTag}, Current: {currentVersion}, IsNewer: {updateAvailable}, DownloadUrl: {downloadUrl ?? "null"}");

                    return new UpdateInfo
                    {
                        UpdateAvailable = updateAvailable && !string.IsNullOrEmpty(downloadUrl),
                        LatestVersion = latestTag,
                        CurrentVersion = currentVersion,
                        DownloadUrl = downloadUrl,
                        ReleaseNotes = releaseNotes
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check exception: {ex}");
                System.Windows.Forms.MessageBox.Show($"Update check failed: {ex.Message}");
                return new UpdateInfo
                {
                    UpdateAvailable = false,
                    CurrentVersion = GetCurrentVersion()
                };
            }
        }

        private static string GetCurrentVersion()
        {
            try
            {
                var version = About.version;
                return version;
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        private static bool IsNewerVersion(string latest, string current)
        {
            try
            {
                // Remove 'v' prefix if present
                latest = latest.TrimStart('v', 'V');
                current = current.TrimStart('v', 'V');

                System.Diagnostics.Debug.WriteLine($"Version comparison - Latest (cleaned): {latest}, Current (cleaned): {current}");

                var latestParts = latest.Split('.');
                var currentParts = current.Split('.');

                int maxLength = Math.Max(latestParts.Length, currentParts.Length);

                for (int i = 0; i < maxLength; i++)
                {
                    int latestPart = i < latestParts.Length && int.TryParse(latestParts[i], out int lp) ? lp : 0;
                    int currentPart = i < currentParts.Length && int.TryParse(currentParts[i], out int cp) ? cp : 0;

                    System.Diagnostics.Debug.WriteLine($"  Part {i}: latest={latestPart}, current={currentPart}");

                    if (latestPart > currentPart)
                        return true;
                    if (latestPart < currentPart)
                        return false;
                }

                return false; // Versions are equal
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Version comparison error: {ex}");
                System.Windows.Forms.MessageBox.Show("Failed to compare versions. Update check may be inaccurate.");
                return false;
            }
        }
    }
}
