using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class AutoStartTests
{
    [Fact]
    public void BuildWindowsCommand_QuotesPathAndAddsFlagWhenMinimized()
    {
        Assert.Equal("\"C:\\app\\SongRequest V2.exe\"", AutoStart.BuildWindowsCommand(@"C:\app\SongRequest V2.exe", startMinimized: false));
        Assert.Equal("\"C:\\app\\SongRequest V2.exe\" --minimized", AutoStart.BuildWindowsCommand(@"C:\app\SongRequest V2.exe", startMinimized: true));
    }

    [Fact]
    public void BuildDesktopEntry_ContainsExecAndFlag()
    {
        var entry = AutoStart.BuildDesktopEntry("/opt/app/SongRequest V2", startMinimized: true);

        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Name=SongRequest", entry);
        Assert.Contains("Exec=\"/opt/app/SongRequest V2\" --minimized", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
    }

    [Fact]
    public void BuildDesktopEntry_OmitsFlagWhenNotMinimized()
    {
        var entry = AutoStart.BuildDesktopEntry("/opt/app/SongRequest V2", startMinimized: false);

        Assert.Contains("Exec=\"/opt/app/SongRequest V2\"", entry);
        Assert.DoesNotContain("--minimized", entry);
    }

    [Fact]
    public void BuildLaunchAgentPlist_ContainsProgramArgumentsAndLabel()
    {
        var plist = AutoStart.BuildLaunchAgentPlist("/Applications/SongRequest V2", startMinimized: true);

        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains("com.songrequest.desktop", plist);
        Assert.Contains("/Applications/SongRequest V2", plist);
        Assert.Contains("--minimized", plist);
        Assert.Contains("RunAtLoad", plist);
    }

    [Fact]
    public void Paths_UseTheAppIdAndProvidedRoots()
    {
        Assert.EndsWith("autostart/songrequest.desktop", AutoStart.DesktopEntryPath("/home/user/.config").Replace('\\', '/'));
        Assert.EndsWith("LaunchAgents/com.songrequest.desktop.plist", AutoStart.LaunchAgentPath("/Users/user").Replace('\\', '/'));
    }

    [Fact]
    public void IsSupported_MatchesDesktopOperatingSystems()
        => Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), AutoStart.IsSupported);
}
