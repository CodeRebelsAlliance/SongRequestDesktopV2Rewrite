using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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

            // Validate before letting Android show "App not installed": a truncated
            // download, a downgrade or an APK signed with a different key all end in
            // that generic message, so explain the real cause instead.
            var problem = VerifyDownloadedApk(path);
            if (problem != null) return problem;

            progress.Report(new UpdateService.UpdateProgress("installing", 100, "Opening the installer…"));
            return OpenInstaller(path);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"update download failed: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Checks the downloaded file is a complete, newer APK of this app signed with
    /// the same key. Returns an explanation for the user, or null when it is fine.
    /// </summary>
    private static string? VerifyDownloadedApk(string path)
    {
        try
        {
            var file = new Java.IO.File(path);
            if (!file.Exists() || file.Length() < 1024 * 1024)
                return "The update download is incomplete. Please try again.";

            // Every APK is a zip: "PK".
            var header = new byte[2];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, 2) != 2 || header[0] != 'P' || header[1] != 'K')
                    return "The downloaded file is not an APK. Please try again.";
            }

            var context = Android.App.Application.Context;
            var manager = context.PackageManager;
            if (manager == null) return null;

            // Installation from this app may be blocked entirely.
            try
            {
                if (!manager.CanRequestPackageInstalls())
                    return "Android blocks installs from SongRequest. Enable \"Install unknown apps\" for SongRequest in Settings, then retry.";
            }
            catch
            {
                // Pre-O has no such switch; the intent path works.
            }

            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.P
                ? PackageInfoFlags.SigningCertificates
                : PackageInfoFlags.Signatures;

            var archive = manager.GetPackageArchiveInfo(path, flags);
            if (archive == null) return "The downloaded file is not a valid APK. Please try again.";
            if (!string.Equals(archive.PackageName, context.PackageName, StringComparison.Ordinal))
                return "The downloaded APK belongs to a different app.";

            var installed = manager.GetPackageInfo(context.PackageName!, flags)!;
            var installedCode = VersionCode(installed);
            var updateCode = VersionCode(archive);
            if (installedCode > 0 && updateCode > 0 && updateCode <= installedCode)
                return $"The downloaded build ({updateCode}) is not newer than the installed one ({installedCode}).";

            var installedSigners = SignerHashes(installed);
            var updateSigners = SignerHashes(archive);
            if (installedSigners.Count > 0 && updateSigners.Count > 0 && !installedSigners.SetEquals(updateSigners))
            {
                // Releases used to be signed with a fresh debug key on every CI run,
                // which makes Android reject the update. One manual install fixes it
                // for all later releases.
                return "This update is signed with a different key than the installed app, so Android cannot install it over this build. " +
                       "Install this APK once by hand (uninstall the old app first if Android asks) — later in-app updates will work.";
            }

            return null;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"update verification skipped: {ex.Message}");
            return null;    // Never block an update because a check failed.
        }
    }

    private static long VersionCode(PackageInfo info)
    {
        try
        {
            return Build.VERSION.SdkInt >= BuildVersionCodes.P
                ? info.LongVersionCode
                : info.VersionCode;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>SHA-256 of every signing certificate, to compare app and APK.</summary>
    private static HashSet<string> SignerHashes(PackageInfo info)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            System.Collections.Generic.IList<Signature>? signatures = null;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                signatures = info.SigningInfo?.GetApkContentsSigners();
            else
                signatures = info.Signatures;

            if (signatures == null) return hashes;

            using var sha = System.Security.Cryptography.SHA256.Create();
            foreach (var signature in signatures)
            {
                var bytes = signature.ToByteArray();
                if (bytes != null) hashes.Add(Convert.ToHexString(sha.ComputeHash(bytes)));
            }
        }
        catch { }

        return hashes;
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
            // Some OEM installers only grant the URI through the clip data.
            try { intent.ClipData = ClipData.NewRawUri("update", uri); } catch { }

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
