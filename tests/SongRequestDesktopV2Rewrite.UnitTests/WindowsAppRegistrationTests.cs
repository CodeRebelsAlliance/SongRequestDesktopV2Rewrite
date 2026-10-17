using System;
using System.IO;
using System.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class WindowsAppRegistrationTests
{
    [Fact]
    public void IsSupported_OnlyOnWindows()
        => Assert.Equal(OperatingSystem.IsWindows(), WindowsAppRegistration.IsSupported);

    [Fact]
    public void Register_IsSafeOnEveryPlatform()
    {
        WindowsAppRegistration.Register();
        WindowsAppRegistration.Unregister();
    }

    [Fact]
    public void RegistryValues_UseTheRequestedNameAndPublisher()
    {
        var values = WindowsAppRegistration.BuildRegistryValues(
            @"C:\Program Files\SongRequest", @"C:\Users\x\AppData\Local\uninstaller\uninstall.cmd",
            existingInstallDate: "20240102", estimatedSizeKb: 1234);

        string? Get(string name) => values.FirstOrDefault(v => v.Name == name)?.StringValue;

        Assert.Equal("SongRequest Desktop", Get("DisplayName"));
        Assert.Equal("CodeRebelsAlliance UG", Get("Publisher"));
        Assert.Equal(AppInfo.version, Get("DisplayVersion"));
        Assert.Equal(AppInfo.version, Get("Version"));
        Assert.Equal(@"C:\Program Files\SongRequest", Get("InstallLocation"));
        Assert.Equal("\"C:\\Users\\x\\AppData\\Local\\uninstaller\\uninstall.cmd\"", Get("UninstallString"));
        Assert.Equal("\"C:\\Users\\x\\AppData\\Local\\uninstaller\\uninstall.cmd\" /quiet", Get("QuietUninstallString"));
        Assert.Equal("20240102", Get("InstallDate")); // preserved across launches
        Assert.Equal("1234", values.First(v => v.Name == "EstimatedSize").DwordValue?.ToString());
    }

    [Fact]
    public void RegistryValues_MarkNoModifyNoRepair_AndParseVersionParts()
    {
        var values = WindowsAppRegistration.BuildRegistryValues("/opt/app", "/tmp/uninstall.cmd", "20240102", 0);

        Assert.Equal(1, values.First(v => v.Name == "NoModify").DwordValue);
        Assert.Equal(1, values.First(v => v.Name == "NoRepair").DwordValue);

        var (major, minor) = WindowsAppRegistration.ParseMajorMinor(AppInfo.version);
        Assert.Equal(major, values.First(v => v.Name == "VersionMajor").DwordValue);
        Assert.Equal(minor, values.First(v => v.Name == "VersionMinor").DwordValue);
        Assert.DoesNotContain(values, v => v.Name == "EstimatedSize"); // omitted when unknown
    }

    [Theory]
    [InlineData("4.0", 4, 0)]
    [InlineData("4.1", 4, 1)]
    [InlineData("12.34.56", 12, 34)]
    [InlineData("7", 7, 0)]
    [InlineData("", 0, 0)]
    [InlineData(null, 0, 0)]
    [InlineData("abc", 0, 0)]
    public void ParseMajorMinor_HandlesEdgeCases(string? version, int major, int minor)
        => Assert.Equal((major, minor), WindowsAppRegistration.ParseMajorMinor(version));

    [Fact]
    public void UninstallScript_UsesTheUninstallKeyAndRemovesAutostart()
    {
        var script = WindowsAppRegistration.BuildUninstallScript(
            @"C:\Apps\SongRequest", @"C:\Users\x\AppData\Roaming\SongRequestDesktopV2Rewrite",
            @"C:\Users\x\AppData\Local\SongRequestDesktopV2Rewrite\uninstaller\", "SongRequest V2.exe");

        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\SongRequestDesktop", script);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", script);
        Assert.Contains("SongRequest Desktop", script);
        Assert.Contains("taskkill /f /im \"SongRequest V2.exe\"", script);
        Assert.Contains("rd /s /q \"%INSTALLDIR%\"", script);
        Assert.Contains("rd /s /q \"%DATADIR%\"", script);
    }

    [Fact]
    public void UninstallScript_SupportsQuietMode_AndAsksBeforeDeleting()
    {
        var script = WindowsAppRegistration.BuildUninstallScript("/opt/app", "/home/x/.config/app", "/tmp/u/", "SongRequest V2");

        Assert.Contains("if /i \"%~1\"==\"/quiet\" set \"QUIET=1\"", script);
        Assert.Contains("if \"%QUIET%\"==\"1\" goto :program", script);
        Assert.Contains("set /p \"REMOVEDATA=", script);
        Assert.Contains("set /p \"DELETEFILES=", script);
        Assert.Contains("if /i \"%DELETEFILES%\"==\"n\" goto :skip", script);
        Assert.Contains("pause", script);
    }

    [Fact]
    public void UninstallScript_QuotesPathsSoSpecialCharactersAreSafe()
    {
        var script = WindowsAppRegistration.BuildUninstallScript(
            @"C:\Apps\Rock & Roll", @"C:\Data\A&B", @"C:\Scripts\A&B\uninstaller\", "SongRequest V2.exe");

        // Every path usage is quoted, so `&` cannot split the batch command.
        Assert.Contains("set \"INSTALLDIR=C:\\Apps\\Rock & Roll\"", script);
        Assert.Contains("if exist \"%INSTALLDIR%\" rd /s /q \"%INSTALLDIR%\"", script);
        Assert.Contains("echo   Program folder : \"%INSTALLDIR%\"", script);
    }

    [Fact]
    public void UninstallScript_DeletesItselfAfterwards()
    {
        var script = WindowsAppRegistration.BuildUninstallScript("/opt/app", "/data", "/scripts/", "SongRequest V2");

        Assert.Contains("start \"\" /b cmd /c \"timeout /t 2 /nobreak >nul & rd /s /q \"\"%SCRIPTDIR%\"\"", script);
    }

    [Fact]
    public void Uninstaller_LivesOutsideTheInstallAndDataFolders()
    {
        var scriptPath = Path.GetFullPath(WindowsAppRegistration.UninstallScriptPath());

        Assert.StartsWith(Path.GetFullPath(AppPaths.LocalAppDataDir), scriptPath);
        Assert.DoesNotContain(Path.GetFullPath(AppPaths.AppDataDir), scriptPath);
        Assert.DoesNotContain(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), scriptPath);
    }

    [Fact]
    public void WriteUninstallScript_CreatesTheFile()
    {
        var path = WindowsAppRegistration.WriteUninstallScript("/opt/app");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var content = File.ReadAllText(path!);
        Assert.Contains("set \"APPNAME=SongRequest Desktop\"", content);
        Assert.Contains("echo  Uninstalling %APPNAME%", content);
        Assert.Contains("/opt/app", content);
    }

    [Fact]
    public void CalculateDirectorySizeKb_SumsFiles()
    {
        var dir = TestSupport.NewTempDir("size");
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[2048]);

        Assert.Equal(4, WindowsAppRegistration.CalculateDirectorySizeKb(dir));
    }

    [Fact]
    public void CalculateDirectorySizeKb_MissingFolder_IsZero()
        => Assert.Equal(0, WindowsAppRegistration.CalculateDirectorySizeKb(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
}
