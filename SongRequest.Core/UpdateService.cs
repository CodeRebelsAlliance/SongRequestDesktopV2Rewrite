using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Checks GitHub releases for a newer build and selects the asset for the
    /// current platform (Windows = <c>.zip</c>, Linux/macOS = <c>.tar.gz</c>).
    /// </summary>
    public class UpdateService
    {
        private const string GITHUB_REPO = "CodeRebelsAlliance/SongRequestDesktopV2Rewrite";
        private const string GITHUB_API_URL = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";

        /// <summary>
        /// Whether this host installs updates itself.
        ///
        /// True on the desktop (download the platform archive, swap the app folder,
        /// relaunch) and on Android (download the release APK and hand it to the
        /// system package installer). False where updates belong to a store — iOS
        /// today — so the app never offers a path it cannot take.
        /// </summary>
        public static bool SelfUpdateSupported { get; set; } = true;

        /// <summary>
        /// Installs a downloaded package on hosts whose updater is the platform's
        /// (Android: download the APK, then the package installer). The host reports
        /// progress and returns an error message, or null on success.
        /// </summary>
        public static Func<string, IProgress<UpdateProgress>, Task<string?>>? PackageInstaller { get; set; }

        /// <summary>Progress reported while a package is downloaded/installed.</summary>
        public readonly record struct UpdateProgress(string Stage, double Percent, string? Message);

        public class UpdateInfo
        {
            public bool UpdateAvailable { get; set; }
            public string LatestVersion { get; set; } = string.Empty;
            public string CurrentVersion { get; set; } = string.Empty;
            public string DownloadUrl { get; set; } = string.Empty;
            public string ReleaseNotes { get; set; } = string.Empty;

            /// <summary>Platform this check ran for, e.g. <c>linux-x64</c>.</summary>
            public string Platform { get; set; } = string.Empty;

            /// <summary>Release asset that was selected, e.g. <c>SongRequest-linux-x64.tar.gz</c>.</summary>
            public string AssetName { get; set; } = string.Empty;

            /// <summary>Set when the check itself failed, or no package exists for this platform.</summary>
            public string? Error { get; set; }
        }

        /// <summary>
        /// Runtime identifier-style platform string, e.g. <c>win-x64</c>,
        /// <c>osx-arm64</c> — or <c>android</c>/<c>ios</c>, whose release assets are
        /// single packages rather than per-architecture archives.
        /// </summary>
        internal static string CurrentPlatform()
        {
            if (OperatingSystem.IsAndroid()) return "android";
            if (OperatingSystem.IsIOS()) return "ios";

            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => "x64",
            };

            if (OperatingSystem.IsWindows()) return "win-" + arch;
            if (OperatingSystem.IsMacOS()) return "osx-" + arch;
            return "linux-" + arch;
        }

        /// <summary>Release asset name the CI produces for a platform.</summary>
        internal static string ExpectedAssetName(string platform)
            => "SongRequest-" + platform + platform switch
            {
                _ when platform.StartsWith("win", StringComparison.OrdinalIgnoreCase) => ".zip",
                _ when platform.Equals("android", StringComparison.OrdinalIgnoreCase) => ".apk",
                _ when platform.Equals("ios", StringComparison.OrdinalIgnoreCase) => ".ipa",
                _ => ".tar.gz",
            };

        /// <summary>
        /// Picks the release asset for the platform. Falls back to the legacy
        /// <c>songrequest.zip</c> asset (Windows-only) for old releases.
        /// </summary>
        internal static (string? Name, string? Url) SelectAsset(JArray? assets, string platform)
        {
            if (assets == null) return (null, null);

            var expected = ExpectedAssetName(platform);
            foreach (var asset in assets)
            {
                if (string.Equals(asset["name"]?.ToString(), expected, StringComparison.OrdinalIgnoreCase))
                    return (asset["name"]?.ToString(), asset["browser_download_url"]?.ToString());
            }

            if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var asset in assets)
                {
                    if (string.Equals(asset["name"]?.ToString(), "songrequest.zip", StringComparison.OrdinalIgnoreCase))
                        return (asset["name"]?.ToString(), asset["browser_download_url"]?.ToString());
                }
            }

            return (null, null);
        }

        public static Task<UpdateInfo> CheckForUpdatesAsync()
            => CheckForUpdatesAsync(null);

        /// <summary>Check for updates with an optional HTTP client (used by tests).</summary>
        internal static async Task<UpdateInfo> CheckForUpdatesAsync(HttpClient? httpClient)
        {
            var platform = CurrentPlatform();
            var ownsClient = httpClient == null;
            var client = httpClient ?? new HttpClient();
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SongRequestApp");
                client.Timeout = TimeSpan.FromSeconds(15);

                var response = await client.GetStringAsync(GITHUB_API_URL).ConfigureAwait(false);
                var json = JObject.Parse(response);

                var latestTag = json["tag_name"]?.ToString() ?? string.Empty;
                var releaseNotes = json["body"]?.ToString() ?? string.Empty;

                var (assetName, downloadUrl) = SelectAsset(json["assets"] as JArray, platform);
                var currentVersion = GetCurrentVersion();
                var updateAvailable = IsNewerVersion(latestTag, currentVersion);

                return new UpdateInfo
                {
                    UpdateAvailable = updateAvailable && !string.IsNullOrEmpty(downloadUrl),
                    LatestVersion = latestTag,
                    CurrentVersion = currentVersion,
                    DownloadUrl = downloadUrl ?? string.Empty,
                    ReleaseNotes = releaseNotes,
                    Platform = platform,
                    AssetName = assetName ?? string.Empty,
                    Error = updateAvailable && string.IsNullOrEmpty(downloadUrl)
                        ? $"No update package is available for {platform}."
                        : null,
                };
            }
            catch (Exception ex)
            {
                return new UpdateInfo
                {
                    UpdateAvailable = false,
                    CurrentVersion = GetCurrentVersion(),
                    Platform = platform,
                    Error = ex.Message,
                };
            }
            finally
            {
                if (ownsClient) client.Dispose();
            }
        }

        internal static string GetCurrentVersion()
        {
            try { return AppInfo.version; }
            catch { return "0.0.0.0"; }
        }

        internal static bool IsNewerVersion(string latest, string current)
        {
            try
            {
                latest = latest.TrimStart('v', 'V');
                current = current.TrimStart('v', 'V');

                var latestParts = latest.Split('.');
                var currentParts = current.Split('.');

                int maxLength = Math.Max(latestParts.Length, currentParts.Length);

                for (int i = 0; i < maxLength; i++)
                {
                    int latestPart = i < latestParts.Length && int.TryParse(latestParts[i], out int lp) ? lp : 0;
                    int currentPart = i < currentParts.Length && int.TryParse(currentParts[i], out int cp) ? cp : 0;

                    if (latestPart > currentPart) return true;
                    if (latestPart < currentPart) return false;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
