using System;
using System.IO;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Linux desktop integration: the app id (Wayland matches a window to its launcher
/// entry by it) and the generated <c>.desktop</c> entry that supplies the icon.
/// </summary>
public class LinuxDesktopIntegrationTests
{
    [Fact]
    public void BuildApplicationEntry_DescribesTheAppAndItsIcon()
    {
        var entry = LinuxDesktopIntegration.BuildApplicationEntry("/opt/SongRequest V2", "/opt/SRappIcon.png");

        Assert.StartsWith("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Name=SongRequest Desktop", entry);
        Assert.Contains("Exec=\"/opt/SongRequest V2\"", entry); // quoted: paths may contain spaces
        Assert.Contains("Icon=/opt/SRappIcon.png", entry);
        Assert.Contains($"StartupWMClass={LinuxDesktopIntegration.AppId}", entry);
        Assert.Contains("Categories=", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void ApplicationEntryPath_UsesTheXdgDataHome()
        => Assert.Equal(
            Path.Combine("/tmp/datahome", "applications", "songrequest.desktop"),
            LinuxDesktopIntegration.ApplicationEntryPath("/tmp/datahome"));

    [Fact]
    public void IsSupported_OnlyOnLinux()
        => Assert.Equal(OperatingSystem.IsLinux(), LinuxDesktopIntegration.IsSupported);

    [Fact]
    public void EnsureApplicationId_IsSafeToCallEverywhere()
        => LinuxDesktopIntegration.EnsureApplicationId();

    [Fact]
    public void ApplyWindowIcon_MissingFile_IsASafeNoOp()
        => LinuxDesktopIntegration.ApplyWindowIcon(
            Path.Combine(Path.GetTempPath(), "no-such-icon-" + Guid.NewGuid().ToString("N") + ".png"));

    [Fact]
    public void EnsureApplicationEntry_WritesTheEntryAndIsIdempotent()
    {
        var dataHome = TestSupport.NewTempDir("xdgdata");

        LinuxDesktopIntegration.EnsureApplicationEntry("/opt/SongRequest V2", "/opt/SRappIcon.png", dataHome);

        var path = LinuxDesktopIntegration.ApplicationEntryPath(dataHome);
        Assert.True(File.Exists(path));
        Assert.Contains("Icon=/opt/SRappIcon.png", File.ReadAllText(path));

        // Calling it again must not throw or corrupt the file.
        LinuxDesktopIntegration.EnsureApplicationEntry("/opt/SongRequest V2", "/opt/SRappIcon.png", dataHome);
        Assert.Contains("Name=SongRequest Desktop", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyWindowIcon_ExistingFile_DoesNotThrow()
    {
        var icon = Path.Combine(TestSupport.NewTempDir("icon"), "icon.png");
        File.WriteAllBytes(icon, new byte[] { 1, 2, 3 });

        LinuxDesktopIntegration.ApplyWindowIcon(icon);
    }
}
