using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Registers the app in the per-user Windows **"Apps &amp; features"** list
/// (Settings → Apps → Installed apps), so users can see the installed version and
/// uninstall it without administrator rights.
///
/// Everything is written under <c>HKEY_CURRENT_USER</c> (no elevation). The entry
/// is refreshed on every launch, which keeps <c>DisplayVersion</c> in sync after an
/// in-app update.
///
/// The uninstall is performed by a generated <c>uninstall.cmd</c> that is kept in
/// the per-user local app-data folder (deliberately outside the install folder, so
/// it can delete the install folder, and outside the roaming data folder, so it can
/// delete that too).
/// </summary>
public static class WindowsAppRegistration
{
    internal const string DisplayName = "SongRequest Desktop";
    internal const string Publisher = "CodeRebelsAlliance UG";
    internal const string Description = "Song request queue, soundboard and playback for live events.";
    internal const string ProductUrl = "https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite";
    internal const string UninstallKeyName = "SongRequestDesktop";

    internal const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;

    internal const string UninstallScriptFileName = "uninstall.cmd";

    public static bool IsSupported => OperatingSystem.IsWindows();

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates/refreshes the "Apps &amp; features" entry and the uninstaller script.
    /// Best effort: never throws.
    /// </summary>
    public static void Register()
    {
        if (!IsSupported) return;
        try
        {
            var installDirectory = AppDomain.CurrentDomain.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var scriptPath = WriteUninstallScript(installDirectory);
            if (scriptPath == null) return;

            WriteRegistry(installDirectory, scriptPath);
        }
        catch
        {
            // Registration is a convenience; never let it break startup.
        }
    }

    /// <summary>Removes the "Apps &amp; features" entry. Best effort.</summary>
    public static void Unregister()
    {
        if (!IsSupported) return;
        try
        {
            DeleteRegistryKey(UninstallKeyPath);
        }
        catch { }
    }

    /// <summary>Folder that holds the generated uninstaller.</summary>
    internal static string UninstallerDirectory()
        => Path.Combine(AppPaths.LocalAppDataDir, "uninstaller");

    /// <summary>Full path of the generated uninstaller.</summary>
    internal static string UninstallScriptPath()
        => Path.Combine(UninstallerDirectory(), UninstallScriptFileName);

    /// <summary>A registry value written for the "Apps &amp; features" entry.</summary>
    internal sealed record RegistryValue(string Name, string? StringValue, int? DwordValue);

    /// <summary>
    /// The values that make up the per-user uninstall entry. Pure so it can be
    /// unit-tested on any platform.
    /// </summary>
    internal static IReadOnlyList<RegistryValue> BuildRegistryValues(
        string installDirectory, string scriptPath, string? existingInstallDate, int estimatedSizeKb)
    {
        var launcher = Path.Combine(installDirectory, UpdateInstaller.LauncherName());
        var (major, minor) = ParseMajorMinor(AppInfo.version);

        var values = new List<RegistryValue>
        {
            new("DisplayName", DisplayName, null),
            new("Publisher", Publisher, null),
            new("DisplayVersion", AppInfo.version, null),
            new("Version", AppInfo.version, null),
            new("Comments", Description, null),
            new("InstallLocation", installDirectory, null),
            new("DisplayIcon", File.Exists(launcher) ? launcher : installDirectory, null),
            new("UninstallString", $"\"{scriptPath}\"", null),
            new("QuietUninstallString", $"\"{scriptPath}\" /quiet", null),
            new("InstallDate", string.IsNullOrEmpty(existingInstallDate) ? DateTime.Now.ToString("yyyyMMdd") : existingInstallDate, null),
            new("URLInfoAbout", ProductUrl, null),
            new("URLUpdateInfo", ProductUrl + "/releases/latest", null),
            new("NoModify", null, 1),
            new("NoRepair", null, 1),
            new("VersionMajor", null, major),
            new("VersionMinor", null, minor),
        };

        if (estimatedSizeKb > 0) values.Add(new RegistryValue("EstimatedSize", null, estimatedSizeKb));
        return values;
    }

    /// <summary>Writes the uninstaller script and returns its path (null on failure).</summary>
    internal static string? WriteUninstallScript(string installDirectory)
    {
        try
        {
            var directory = UninstallerDirectory();
            Directory.CreateDirectory(directory);

            var scriptDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            var content = BuildUninstallScript(
                installDirectory,
                AppPaths.AppDataDir,
                scriptDirectory,
                UpdateInstaller.LauncherName());

            var path = UninstallScriptPath();
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ── Pure builders (unit-tested) ────────────────────────────────────────

    /// <summary>
    /// Builds the uninstall batch script. Paths are always quoted so characters
    /// like <c>&amp;</c> cannot break the batch parser.
    /// </summary>
    internal static string BuildUninstallScript(
        string installDirectory, string dataDirectory, string scriptDirectory, string launcherName)
    {
        var sb = new StringBuilder();

        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine("title Uninstall " + DisplayName);
        sb.AppendLine($"set \"APPNAME={DisplayName}\"");
        sb.AppendLine($"set \"INSTALLDIR={installDirectory}\"");
        sb.AppendLine($"set \"DATADIR={dataDirectory}\"");
        sb.AppendLine("set \"SCRIPTDIR=%~dp0\"");
        sb.AppendLine($"set \"UNINSTKEY=HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{UninstallKeyName}\"");
        sb.AppendLine("set \"RUNKEY=HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\"");
        sb.AppendLine($"set \"RUNVALUE={AutoStart.ValueName}\"");
        sb.AppendLine("set \"QUIET=0\"");
        sb.AppendLine("if /i \"%~1\"==\"/quiet\" set \"QUIET=1\"");
        sb.AppendLine();
        sb.AppendLine("echo(");
        sb.AppendLine("echo  Uninstalling %APPNAME%");
        sb.AppendLine("echo  ------------------------------------------------------------");
        sb.AppendLine("echo   Program folder : \"%INSTALLDIR%\"");
        sb.AppendLine("echo   Settings folder: \"%DATADIR%\"");
        sb.AppendLine("echo(");
        sb.AppendLine();
        sb.AppendLine("rem 1. Stop a running instance (best effort).");
        sb.AppendLine($"taskkill /f /im \"{launcherName}\" >nul 2>&1");
        sb.AppendLine();
        sb.AppendLine("rem 2. Remove the Windows \"Apps ^& features\" entry and the autostart entry.");
        sb.AppendLine("reg delete \"%UNINSTKEY%\" /f >nul 2>&1");
        sb.AppendLine("reg delete \"%RUNKEY%\" /v \"%RUNVALUE%\" /f >nul 2>&1");
        sb.AppendLine();
        sb.AppendLine("rem 3. Personal data (settings, library, downloads, saved login) - optional.");
        sb.AppendLine("if \"%QUIET%\"==\"1\" goto :program");
        sb.AppendLine("set \"REMOVEDATA=N\"");
        sb.AppendLine("set /p \"REMOVEDATA=Also delete your settings, library, downloads and saved login? [y/N] \"");
        sb.AppendLine("if /i not \"%REMOVEDATA%\"==\"y\" goto :program");
        sb.AppendLine("echo Removing personal data from \"%DATADIR%\"...");
        sb.AppendLine("if exist \"%DATADIR%\" rd /s /q \"%DATADIR%\" >nul 2>&1");
        sb.AppendLine();
        sb.AppendLine("rem 4. Program folder - always confirmed in interactive mode, because the");
        sb.AppendLine("rem    archive is extracted wherever the user chose.");
        sb.AppendLine(":program");
        sb.AppendLine("if \"%QUIET%\"==\"1\" goto :deleteprogram");
        sb.AppendLine("set \"DELETEFILES=Y\"");
        sb.AppendLine("set /p \"DELETEFILES=Delete the program folder and everything in it? [Y/n] \"");
        sb.AppendLine("if /i \"%DELETEFILES%\"==\"n\" goto :skip");
        sb.AppendLine();
        sb.AppendLine(":deleteprogram");
        sb.AppendLine("echo Removing program files from \"%INSTALLDIR%\"...");
        sb.AppendLine("if exist \"%INSTALLDIR%\" rd /s /q \"%INSTALLDIR%\" >nul 2>&1");
        sb.AppendLine("goto :selfdelete");
        sb.AppendLine();
        sb.AppendLine(":skip");
        sb.AppendLine("echo Skipped. You can remove \"%INSTALLDIR%\" yourself.");
        sb.AppendLine();
        sb.AppendLine("rem 5. Remove this uninstaller once we have exited.");
        sb.AppendLine(":selfdelete");
        sb.AppendLine("start \"\" /b cmd /c \"timeout /t 2 /nobreak >nul & rd /s /q \"\"%SCRIPTDIR%\"\" >nul 2>&1\"");
        sb.AppendLine();
        sb.AppendLine("echo(");
        sb.AppendLine("echo  %APPNAME% has been uninstalled.");
        sb.AppendLine("echo(");
        sb.AppendLine("if \"%QUIET%\"==\"1\" goto :done");
        sb.AppendLine("pause");
        sb.AppendLine(":done");
        sb.AppendLine("endlocal");
        sb.AppendLine("exit /b 0");

        return sb.ToString();
    }

    /// <summary>Parses <c>major.minor</c> out of a version string.</summary>
    internal static (int Major, int Minor) ParseMajorMinor(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    /// <summary>Approximate size of the install folder in KB (0 when unavailable).</summary>
    internal static int CalculateDirectorySizeKb(string directory, int maxFiles = 5000)
    {
        try
        {
            long total = 0;
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (++count > maxFiles) break;
                try { total += new FileInfo(file).Length; } catch { }
            }

            return (int)Math.Min(total / 1024, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    // ── Windows registry (advapi32 P/Invoke, no extra package) ──────────────

    private const int KEY_WRITE = 0x20006;
    private const int REG_SZ = 1;
    private const int REG_DWORD = 4;

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string subKey, int reserved, string? lpClass,
        int options, int samDesired, IntPtr securityAttributes, out IntPtr phkResult, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, int reserved, int type,
        byte[] data, int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out int type,
        byte[]? data, ref int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteTreeW(IntPtr hKey, string subKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    private static void WriteRegistry(string installDirectory, string scriptPath)
    {
        if (RegCreateKeyExW(HKEY_CURRENT_USER, UninstallKeyPath, 0, null, 0, KEY_WRITE, IntPtr.Zero,
                out var key, out _) != 0)
        {
            return;
        }

        try
        {
            var existingInstallDate = ReadString(key, "InstallDate");
            var sizeKb = CalculateDirectorySizeKb(installDirectory);

            foreach (var value in BuildRegistryValues(installDirectory, scriptPath, existingInstallDate, sizeKb))
            {
                if (value.DwordValue is int dword)
                    SetDword(key, value.Name, dword);
                else
                    SetString(key, value.Name, value.StringValue ?? string.Empty);
            }
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void DeleteRegistryKey(string subKey)
    {
        RegDeleteTreeW(HKEY_CURRENT_USER, subKey);
    }

    private static void SetString(IntPtr key, string name, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        RegSetValueExW(key, name, 0, REG_SZ, bytes, bytes.Length);
    }

    private static void SetDword(IntPtr key, string name, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        RegSetValueExW(key, name, 0, REG_DWORD, bytes, bytes.Length);
    }

    private static string? ReadString(IntPtr key, string name)
    {
        int size = 0;
        if (RegQueryValueExW(key, name, IntPtr.Zero, out _, null, ref size) != 0 || size <= 0) return null;

        var buffer = new byte[size];
        if (RegQueryValueExW(key, name, IntPtr.Zero, out _, buffer, ref size) != 0) return null;

        var value = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
