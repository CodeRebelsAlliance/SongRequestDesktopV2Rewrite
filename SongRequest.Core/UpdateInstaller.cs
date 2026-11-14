using System;
using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Downloads and applies application updates without any WPF dependency.
///
/// Cross-platform: Windows release assets are <c>.zip</c>, Linux/macOS assets are
/// <c>.tar.gz</c>. The download is verified before use, the running install is
/// backed up, and the updater script rolls back if the new build fails to apply.
/// Progress is reported through <see cref="UpdateInstallProgress"/> stages.
/// </summary>
public static class UpdateInstaller
{
    public static class Stages
    {
        public const string Starting = "starting";
        public const string Downloading = "downloading";
        public const string Verifying = "verifying";
        public const string Extracting = "extracting";
        public const string Preparing = "preparing";
        public const string Restarting = "restarting";
        public const string Done = "done";
        public const string Error = "error";
    }

    public sealed class UpdateInstallProgress
    {
        public string Stage { get; set; } = string.Empty;
        public double Percent { get; set; }
        public string? Message { get; set; }
    }

    public static Task InstallAsync(string downloadUrl, Action<UpdateInstallProgress> onProgress)
    {
        if (LinuxPackaging.IsAppImage)
            return InstallAppImageAsync(downloadUrl, onProgress);

        var platform = UpdateService.CurrentPlatform();
        var extension = ArchiveExtensionFor(platform);

        return InstallCoreAsync(
            downloadUrl,
            onProgress,
            extension,
            download: DownloadAsync,
            verify: VerifyArchiveAsync,
            extract: ExtractArchiveAsync,
            startUpdater: StartUpdater,
            exit: () => Environment.Exit(0));
    }

    /// <summary>
    /// AppImage self-update: download the new AppImage, verify it is an ELF, then
    /// let a detached script replace the original file and relaunch it.
    /// </summary>
    private static Task InstallAppImageAsync(string downloadUrl, Action<UpdateInstallProgress> onProgress)
    {
        var appImagePath = LinuxPackaging.AppImagePath;
        if (string.IsNullOrWhiteSpace(appImagePath) || !File.Exists(appImagePath))
            throw new InvalidOperationException("The running AppImage could not be located.");

        if (!CanReplace(appImagePath))
            throw new InvalidOperationException(
                "The AppImage file is not writable. Move it to a writable folder (for example your home folder) and try again.");

        return InstallAppImageCoreAsync(
            downloadUrl,
            appImagePath!,
            onProgress,
            download: DownloadAsync,
            verify: VerifyAppImageAsync,
            startUpdater: StartAppImageUpdater,
            exit: () => Environment.Exit(0));
    }

    /// <summary>
    /// AppImage orchestration with injectable side effects (unit-tested).
    /// </summary>
    internal static async Task InstallAppImageCoreAsync(
        string downloadUrl,
        string appImagePath,
        Action<UpdateInstallProgress> onProgress,
        Func<string, string, Action<double>, Task> download,
        Func<string, Task> verify,
        Action<string, string> startUpdater,
        Action exit)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("No download URL provided.");

        void Report(string stage, double pct, string? message = null) =>
            onProgress?.Invoke(new UpdateInstallProgress { Stage = stage, Percent = pct, Message = message });

        try
        {
            Report(Stages.Starting, 0, "Preparing update...");

            var newAppImage = Path.Combine(Path.GetTempPath(), "songrequest_update.AppImage");
            TryDeleteFile(newAppImage);

            Report(Stages.Downloading, 0, "Starting download...");
            await download(downloadUrl, newAppImage, pct =>
                Report(Stages.Downloading, pct, $"Downloading... {pct:F1}%")).ConfigureAwait(false);

            Report(Stages.Verifying, 100, "Verifying update...");
            await verify(newAppImage).ConfigureAwait(false);

            Report(Stages.Preparing, 100, "Preparing to install...");
            startUpdater(appImagePath, newAppImage);

            Report(Stages.Restarting, 100, "Restarting to finish the update...");
            await Task.Delay(500).ConfigureAwait(false);

            Report(Stages.Done, 100, "Update complete. Restarting...");
            exit();
        }
        catch (Exception ex)
        {
            Report(Stages.Error, 0, ex.Message);
            throw;
        }
    }

    internal static string ArchiveExtensionFor(string platform)
        => platform.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";

    /// <summary>
    /// Orchestrates an update with injectable side effects so the sequence can be
    /// tested without downloading, extracting, launching a script, or exiting.
    /// </summary>
    internal static async Task InstallCoreAsync(
        string downloadUrl,
        Action<UpdateInstallProgress> onProgress,
        string archiveExtension,
        Func<string, string, Action<double>, Task> download,
        Func<string, Task> verify,
        Func<string, string, Task> extract,
        Action<string> startUpdater,
        Action exit)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("No download URL provided.");

        void Report(string stage, double pct, string? message = null) =>
            onProgress?.Invoke(new UpdateInstallProgress { Stage = stage, Percent = pct, Message = message });

        try
        {
            Report(Stages.Starting, 0, "Preparing update...");

            var archivePath = Path.Combine(Path.GetTempPath(), "songrequest_update" + archiveExtension);
            TryDeleteFile(archivePath);

            Report(Stages.Downloading, 0, "Starting download...");
            await download(downloadUrl, archivePath, pct => Report(Stages.Downloading, pct, $"Downloading... {pct:F1}%"));

            Report(Stages.Verifying, 100, "Verifying download...");
            await verify(archivePath).ConfigureAwait(false);

            Report(Stages.Extracting, 100, "Extracting update...");
            var stagingPath = Path.Combine(Path.GetTempPath(), "SongRequestUpdate");
            if (Directory.Exists(stagingPath))
            {
                try { Directory.Delete(stagingPath, true); } catch { }
            }
            Directory.CreateDirectory(stagingPath);
            await extract(archivePath, stagingPath).ConfigureAwait(false);

            Report(Stages.Preparing, 100, "Preparing to install...");
            startUpdater(stagingPath);

            Report(Stages.Restarting, 100, "Restarting to finish the update...");
            await Task.Delay(500).ConfigureAwait(false);

            Report(Stages.Done, 100, "Update complete. Restarting...");
            exit();
        }
        catch (Exception ex)
        {
            Report(Stages.Error, 0, ex.Message);
            throw;
        }
    }

    // ── Download ───────────────────────────────────────────────────────────

    private static async Task DownloadAsync(string url, string destination, Action<double> onProgress)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await DownloadAsync(url, destination, onProgress, client).ConfigureAwait(false);
    }

    /// <summary>Downloads to a file, reporting 0-100% (injectable client, used by tests).</summary>
    internal static async Task DownloadAsync(string url, string destination, Action<double> onProgress, HttpClient client)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;

        using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var fileStream = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long totalRead = 0;
            int lastPercent = -1;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var percent = (double)totalRead / totalBytes * 100;
                    // One update per whole percent: a 1 MB buffer used to fire
                    // thousands of bridge events for a large AppImage.
                    var rounded = (int)percent;
                    if (rounded != lastPercent)
                    {
                        lastPercent = rounded;
                        onProgress?.Invoke(percent);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ── Verify / extract ───────────────────────────────────────────────────

    internal static Task VerifyArchiveAsync(string archivePath)
    {
        VerifyArchive(archivePath);
        return Task.CompletedTask;
    }

    /// <summary>Throws when the downloaded file is missing, empty or not a zip/gzip archive.</summary>
    internal static void VerifyArchive(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new InvalidOperationException("The downloaded update file is missing.");

        var info = new FileInfo(archivePath);
        if (info.Length < 16)
            throw new InvalidOperationException("The downloaded update file is empty or truncated.");

        Span<byte> magic = stackalloc byte[2];
        using var stream = File.OpenRead(archivePath);
        int read = stream.Read(magic);
        if (read < 2)
            throw new InvalidOperationException("The downloaded update file is not a valid archive.");

        bool isZip = magic[0] == 0x50 && magic[1] == 0x4B;   // "PK"
        bool isGzip = magic[0] == 0x1F && magic[1] == 0x8B;  // gzip
        if (!isZip && !isGzip)
            throw new InvalidOperationException("The downloaded update file is not a valid zip or tar.gz archive.");
    }

    internal static Task VerifyAppImageAsync(string appImagePath)
    {
        VerifyAppImage(appImagePath);
        return Task.CompletedTask;
    }

    /// <summary>Throws when the downloaded file is not an executable ELF/AppImage.</summary>
    internal static void VerifyAppImage(string appImagePath)
    {
        if (!File.Exists(appImagePath))
            throw new InvalidOperationException("The downloaded AppImage is missing.");

        var info = new FileInfo(appImagePath);
        if (info.Length < 16)
            throw new InvalidOperationException("The downloaded AppImage is empty or truncated.");

        Span<byte> magic = stackalloc byte[4];
        using var stream = File.OpenRead(appImagePath);
        int read = stream.Read(magic);
        if (read < 4 || magic[0] != 0x7F || magic[1] != 0x45 || magic[2] != 0x4C || magic[3] != 0x46)
            throw new InvalidOperationException("The downloaded file is not a valid AppImage (missing ELF header).");
    }

    internal static async Task ExtractArchiveAsync(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
        }

        await Task.CompletedTask;
    }

    // ── Apply ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the platform updater script that (after this process exits)
    /// backs up the current install, copies the staged files in, verifies the
    /// launcher exists and rolls back on failure, then restarts the app.
    /// </summary>
    private static void StartUpdater(string stagingPath)
    {
        var appPath = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var launcher = ResolveLauncher(appPath);
        var backupPath = Path.Combine(Path.GetTempPath(), "SongRequestBackup");
        var logPath = Path.Combine(Path.GetTempPath(), "songrequest_update.log");

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "update_songrequest.bat");
            File.WriteAllText(scriptPath, BuildWindowsUpdaterScript(appPath, stagingPath, launcher, backupPath, logPath));
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        else
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "update_songrequest.sh");
            File.WriteAllText(scriptPath, BuildUnixUpdaterScript(appPath, stagingPath, launcher, backupPath, logPath, scriptPath));
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo { FileName = scriptPath, UseShellExecute = true });
            }
        }
    }

    /// <summary>True when the current user may replace the file at <paramref name="path"/>.</summary>
    internal static bool CanReplace(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches a detached script that replaces the running AppImage (after this
    /// process exits), rolls back a failed copy, and relaunches the AppImage.
    /// </summary>
    private static void StartAppImageUpdater(string appImagePath, string newAppImagePath)
    {
        var backupPath = Path.Combine(Path.GetTempPath(), "songrequest_update.backup.AppImage");
        var logPath = Path.Combine(Path.GetTempPath(), "songrequest_update.log");
        var scriptPath = Path.Combine(Path.GetTempPath(), "update_songrequest_appimage.sh");

        File.WriteAllText(
            scriptPath,
            BuildAppImageUpdaterScript(appImagePath, newAppImagePath, backupPath, logPath, scriptPath));
        MakeExecutable(scriptPath);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = scriptPath, UseShellExecute = true });
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    /// <summary>Builds the AppImage replacement script (extracted for testing).</summary>
    internal static string BuildAppImageUpdaterScript(
        string appImagePath,
        string newAppImagePath,
        string backupPath,
        string logPath,
        string scriptPath)
    {
        var lines = new[]
        {
            "#!/bin/sh",
            $"APPIMAGE=\"{appImagePath}\"",
            $"NEW=\"{newAppImagePath}\"",
            $"BACKUP=\"{backupPath}\"",
            $"LOG=\"{logPath}\"",
            $"SELF=\"{scriptPath}\"",
            "",
            "log() { echo \"$(date '+%Y-%m-%d %H:%M:%S') $*\" >> \"$LOG\"; }",
            "log \"SongRequest AppImage updater starting\"",
            "",
            "# Wait (bounded) for the application to exit",
            "i=0",
            "while pgrep -f \"$APPIMAGE\" >/dev/null 2>&1 && [ \"$i\" -lt 60 ]; do",
            "  sleep 0.5",
            "  i=$((i + 1))",
            "done",
            "",
            "pkill -f \"$APPIMAGE\" >/dev/null 2>&1",
            "",
            "# Keep the previous AppImage so a failed replacement can roll back",
            "cp -f \"$APPIMAGE\" \"$BACKUP\" >> \"$LOG\" 2>&1",
            "",
            "# Replace atomically: write next to the target, then rename over it",
            "if cp -f \"$NEW\" \"$APPIMAGE.new\" >> \"$LOG\" 2>&1 && chmod +x \"$APPIMAGE.new\" >> \"$LOG\" 2>&1 && mv -f \"$APPIMAGE.new\" \"$APPIMAGE\" >> \"$LOG\" 2>&1; then",
            "  log \"AppImage replaced\"",
            "else",
            "  log \"Replacement failed - restoring backup\"",
            "  rm -f \"$APPIMAGE.new\"",
            "  cp -f \"$BACKUP\" \"$APPIMAGE\" >> \"$LOG\" 2>&1",
            "  chmod +x \"$APPIMAGE\" >> \"$LOG\" 2>&1",
            "fi",
            "",
            "# Relaunch",
            "nohup \"$APPIMAGE\" >/dev/null 2>&1 &",
            "",
            "# Cleanup",
            "rm -f \"$NEW\" \"$BACKUP\" \"$SELF\"",
        };

        return string.Join("\n", lines);
    }

    /// <summary>The published launcher executable for this platform.</summary>
    internal static string ResolveLauncher(string appPath)
    {
        var name = LauncherName();
        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) &&
            string.Equals(Path.GetFileName(process), name, StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }
        return Path.Combine(appPath, name);
    }

    internal static string LauncherName() => OperatingSystem.IsWindows() ? "SongRequest V2.exe" : "SongRequest V2";

    /// <summary>Builds the Windows update batch script (extracted for testing).</summary>
    internal static string BuildWindowsUpdaterScript(string appPath, string stagingPath, string launcherPath, string backupPath, string logPath)
    {
        var exeName = Path.GetFileName(launcherPath);

        var lines = new[]
        {
            "@echo off",
            "setlocal enableextensions",
            $"set \"APP={appPath}\"",
            $"set \"STAGE={stagingPath}\"",
            $"set \"BACKUP={backupPath}\"",
            $"set \"LAUNCHER={launcherPath}\"",
            $"set \"LOG={logPath}\"",
            "",
            "echo SongRequest updater starting >> \"%LOG%\"",
            "",
            "REM Wait (bounded) for the application to exit",
            "set /a tries=0",
            ":waitloop",
            $"tasklist /FI \"IMAGENAME eq {exeName}\" 2>nul | find /I \"{exeName}\" >nul",
            "if errorlevel 1 goto appclosed",
            "set /a tries+=1",
            "if %tries% GEQ 60 goto appclosed",
            "timeout /t 1 /nobreak >nul",
            "goto waitloop",
            ":appclosed",
            "",
            "REM Kill any remaining instances",
            $"taskkill /F /IM \"{exeName}\" >> \"%LOG%\" 2>&1",
            "",
            "REM Back up the current install",
            "if exist \"%BACKUP%\" rmdir /S /Q \"%BACKUP%\" >> \"%LOG%\" 2>&1",
            "mkdir \"%BACKUP%\" >> \"%LOG%\" 2>&1",
            "xcopy \"%APP%\\*\" \"%BACKUP%\\\" /E /Y /I /Q >> \"%LOG%\" 2>&1",
            "",
            "REM Apply the update",
            "xcopy \"%STAGE%\\*\" \"%APP%\\\" /E /Y /I /Q >> \"%LOG%\" 2>&1",
            "",
            "REM Verify the launcher exists; roll back if not",
            "if exist \"%LAUNCHER%\" goto verified",
            "echo Verification failed - rolling back >> \"%LOG%\"",
            "xcopy \"%BACKUP%\\*\" \"%APP%\\\" /E /Y /I /Q >> \"%LOG%\" 2>&1",
            ":verified",
            "",
            "REM Relaunch the application",
            "start \"\" \"%LAUNCHER%\"",
            "",
            "REM Cleanup",
            "timeout /t 2 /nobreak >nul",
            "rmdir /S /Q \"%STAGE%\" >> \"%LOG%\" 2>&1",
            "rmdir /S /Q \"%BACKUP%\" >> \"%LOG%\" 2>&1",
            "del \"%~f0\"",
        };

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Builds the Unix update shell script (extracted for testing).</summary>
    internal static string BuildUnixUpdaterScript(string appPath, string stagingPath, string launcherPath, string backupPath, string logPath, string scriptPath)
    {
        var lines = new[]
        {
            "#!/bin/sh",
            $"APP=\"{appPath}\"",
            $"STAGE=\"{stagingPath}\"",
            $"BACKUP=\"{backupPath}\"",
            $"LAUNCHER=\"{launcherPath}\"",
            $"LOG=\"{logPath}\"",
            $"SELF=\"{scriptPath}\"",
            "",
            "log() { echo \"$(date '+%Y-%m-%d %H:%M:%S') $*\" >> \"$LOG\"; }",
            "log \"SongRequest updater starting\"",
            "",
            "# Wait (bounded) for the application to exit",
            "i=0",
            "while pgrep -f \"$LAUNCHER\" >/dev/null 2>&1 && [ \"$i\" -lt 60 ]; do",
            "  sleep 0.5",
            "  i=$((i + 1))",
            "done",
            "",
            "# Kill any remaining instances",
            "pkill -f \"$LAUNCHER\" >/dev/null 2>&1",
            "",
            "# Back up the current install",
            "rm -rf \"$BACKUP\"",
            "mkdir -p \"$BACKUP\"",
            "cp -Rf \"$APP\"/. \"$BACKUP\"/ >> \"$LOG\" 2>&1",
            "",
            "# Apply the update",
            "cp -Rf \"$STAGE\"/. \"$APP\"/ >> \"$LOG\" 2>&1",
            "",
            "# Verify the launcher exists; roll back if not",
            "if [ ! -f \"$LAUNCHER\" ]; then",
            "  log \"Verification failed - rolling back\"",
            "  cp -Rf \"$BACKUP\"/. \"$APP\"/ >> \"$LOG\" 2>&1",
            "fi",
            "",
            "# Relaunch the application",
            "chmod +x \"$LAUNCHER\" >/dev/null 2>&1",
            "nohup \"$LAUNCHER\" >/dev/null 2>&1 &",
            "",
            "# Cleanup",
            "rm -rf \"$STAGE\" \"$BACKUP\"",
            "rm -f \"$SELF\"",
        };

        return string.Join("\n", lines);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
