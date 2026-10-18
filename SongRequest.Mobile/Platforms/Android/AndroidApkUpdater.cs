using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Core.Content;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Android in-app update: download the release APK and hand it to the system
/// package installer.
///
/// A phone cannot swap its own app folder the way the desktop does, so the flow is
/// the platform's: fetch the APK with progress, expose it through a
/// <c>FileProvider</c> (a raw <c>file://</c> URI is refused on modern Android) and
/// open the installer. Android shows its own "install this update?" prompt, and the
/// user's decision is final — the app never installs silently.
/// </summary>
internal static class AndroidApkUpdater
{
    /// <summary>Where the downloaded package waits for the installer.</summary>
    private static string TargetPath => Path.Combine(Android.App.Application.Context.CacheDir?.AbsolutePath ?? AppPaths.DataRoot, "updates");

    /// <summary>
    /// Downloads <paramref name="downloadUrl"/> and opens the installer.
    /// Returns null on success, or a message the UI can show.
    /// </summary>
    public static async Task<string?> InstallAsync(string downloadUrl, IProgress<UpdateService.UpdateProgress> progress)
    {
        try
        {
            var directory = TargetPath;
            Directory.CreateDirectory(directory);

            var fileName = SafeFileName(downloadUrl);
            var path = Path.Combine(directory, fileName);

            progress.Report(new UpdateService.UpdateProgress("downloading", 0, "Downloading update…"));

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SongRequestApp");

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var destination = File.Create(path);

                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                    read += count;

                    var percent = total > 0 ? read * 100.0 / total : 0;
                    progress.Report(new UpdateService.UpdateProgress("downloading", percent,
                        total > 0 ? $"Downloading update… {percent:F0}%" : "Downloading update…"));
                }
            }

            progress.Report(new UpdateService.UpdateProgress("installing", 100, "Opening the installer…"));
            return OpenInstaller(path);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"update download failed: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Hands the downloaded APK to Android's package installer.</summary>
    private static string? OpenInstaller(string path)
    {
        try
        {
            var context = Android.App.Application.Context;
            var file = new Java.IO.File(path);
            if (!file.Exists()) return "The downloaded update is missing.";

            var authority = context.PackageName + ".updates";
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, file);
            if (uri == null) return "The update could not be shared with the installer.";

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

            context.StartActivity(intent);
            Android.Util.Log.Debug("SongRequest", $"update handed to the package installer: {path}");
            return null;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"opening the installer failed: {ex.Message}");
            return ex.Message;
        }
    }

    private static string SafeFileName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "update.apk" : name;
        }
        catch
        {
            return "update.apk";
        }
    }
}
