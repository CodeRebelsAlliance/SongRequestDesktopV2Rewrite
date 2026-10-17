using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform "start with the system" registration.
///
/// Windows: <c>HKCU\...\CurrentVersion\Run</c> (via advapi32, no extra package).
/// Linux:   <c>~/.config/autostart/songrequest.desktop</c>.
/// macOS:   <c>~/Library/LaunchAgents/com.songrequest.desktop.plist</c>.
/// </summary>
public static class AutoStart
{
    internal const string AppId = "songrequest";
    internal const string ValueName = "SongRequest";
    internal const string MinimizedFlag = "--minimized";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Full path to the launcher that should be started at login.</summary>
    public static string LauncherPath()
    {
        var name = UpdateInstaller.LauncherName();
        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process) &&
            string.Equals(Path.GetFileName(process), name, StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        var appPath = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(appPath, name);
    }

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindowsRunValue() != null;
            if (OperatingSystem.IsMacOS()) return File.Exists(LaunchAgentPath());
            if (OperatingSystem.IsLinux()) return File.Exists(DesktopEntryPath());
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        try
        {
            var launcher = LauncherPath();

            if (OperatingSystem.IsWindows())
            {
                WriteWindowsRunValue(enabled ? BuildWindowsCommand(launcher, startMinimized) : null);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var path = LaunchAgentPath();
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, BuildLaunchAgentPlist(launcher, startMinimized));
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var path = DesktopEntryPath();
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, BuildDesktopEntry(launcher, startMinimized));
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Autostart is best-effort; failures must never break the app.
        }
    }

    // ── Pure builders (unit-tested) ────────────────────────────────────────

    internal static string BuildWindowsCommand(string launcherPath, bool startMinimized)
        => startMinimized ? $"\"{launcherPath}\" {MinimizedFlag}" : $"\"{launcherPath}\"";

    internal static string BuildDesktopEntry(string launcherPath, bool startMinimized)
    {
        var exec = startMinimized ? $"\"{launcherPath}\" {MinimizedFlag}" : $"\"{launcherPath}\"";
        return string.Join("\n", new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=SongRequest",
            "Comment=SongRequest Desktop",
            $"Exec={exec}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
        }) + "\n";
    }

    internal static string BuildLaunchAgentPlist(string launcherPath, bool startMinimized)
    {
        var args = startMinimized
            ? $"<string>{launcherPath}</string><string>{MinimizedFlag}</string>"
            : $"<string>{launcherPath}</string>";

        return string.Join("\n", new[]
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "    <key>Label</key>",
            $"    <string>com.{AppId}.desktop</string>",
            "    <key>ProgramArguments</key>",
            $"    <array>{args}</array>",
            "    <key>RunAtLoad</key>",
            "    <true/>",
            "</dict>",
            "</plist>",
        }) + "\n";
    }

    internal static string DesktopEntryPath(string? configHome = null)
        => Path.Combine(configHome ?? DefaultConfigHome(), "autostart", AppId + ".desktop");

    internal static string LaunchAgentPath(string? home = null)
        => Path.Combine(home ?? DefaultHome(), "Library", "LaunchAgents", $"com.{AppId}.desktop.plist");

    private static string DefaultConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg)) return xdg;
        return Path.Combine(DefaultHome(), ".config");
    }

    private static string DefaultHome()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ── Windows registry (advapi32 P/Invoke) ───────────────────────────────

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int KEY_READ = 0x20019;
    private const int KEY_WRITE = 0x20006;
    private const int REG_SZ = 1;

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, int reserved, int type, byte[] data, int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string valueName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out int type, byte[]? data, ref int dataSize);

    private static string? ReadWindowsRunValue()
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, RunKey, 0, KEY_READ, out var key) != 0) return null;
        try
        {
            int size = 0;
            if (RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, null, ref size) != 0 || size <= 0) return null;

            var buffer = new byte[size];
            if (RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, buffer, ref size) != 0) return null;

            var value = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            return string.IsNullOrEmpty(value) ? null : value;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void WriteWindowsRunValue(string? command)
    {
        if (RegOpenKeyExW(HKEY_CURRENT_USER, RunKey, 0, KEY_WRITE, out var key) != 0) return;
        try
        {
            if (string.IsNullOrEmpty(command))
            {
                RegDeleteValueW(key, ValueName);
            }
            else
            {
                var bytes = Encoding.Unicode.GetBytes(command + "\0");
                RegSetValueExW(key, ValueName, 0, REG_SZ, bytes, bytes.Length);
            }
        }
        finally
        {
            RegCloseKey(key);
        }
    }
}
