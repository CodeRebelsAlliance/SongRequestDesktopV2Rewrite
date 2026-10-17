using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class TrayTests
{
    [Fact]
    public void Menu_HasTheExpectedCommandsInOrder()
    {
        var items = TrayMenu.Build();

        Assert.Equal(
            new[] { TrayCommand.Show, TrayCommand.TogglePlayPause, TrayCommand.Next, TrayCommand.Stop, TrayCommand.Exit },
            items.Select(i => i.Command));
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Label)));
    }

    [Fact]
    public void NullTray_ReportsUnsupportedAndRaisesCommands()
    {
        var tray = new NullTrayIcon();
        Assert.False(tray.IsSupported);

        TrayCommand? received = null;
        tray.CommandInvoked += (_, command) => received = command;

        tray.Raise(TrayCommand.Exit);

        Assert.Equal(TrayCommand.Exit, received);
        tray.Dispose();
    }

    [Fact]
    public void Factory_SupportMatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows()
            || (OperatingSystem.IsLinux() && AppIndicatorNative.IsAvailable);
        Assert.Equal(expected, TrayIconFactory.IsSupported);
    }
}

public class GlobalHotkeyBackendFactoryTests
{
    [Fact]
    public void Factory_ReturnsABackendWhoseSupportMatches()
    {
        var backend = GlobalHotkeyBackendFactory.Create();
        Assert.NotNull(backend);
        Assert.Equal(backend.IsSupported, GlobalHotkeyBackendFactory.IsSupported);
    }
}

public class AppLaunchArgsTests
{
    [Fact]
    public void Parse_DetectsTheMinimizedFlag()
    {
        try
        {
            AppLaunchArgs.Parse(new[] { "--minimized" });
            Assert.True(AppLaunchArgs.StartMinimized);

            AppLaunchArgs.Parse(new[] { "--other" });
            Assert.False(AppLaunchArgs.StartMinimized);

            AppLaunchArgs.Parse(new[] { "--MINIMIZED" });
            Assert.True(AppLaunchArgs.StartMinimized);

            AppLaunchArgs.Parse(null);
            Assert.False(AppLaunchArgs.StartMinimized);
        }
        finally
        {
            AppLaunchArgs.Parse(Array.Empty<string>());
        }
    }

    [Fact]
    public void Parse_DetectsTheGraphicsFlags()
    {
        try
        {
            AppLaunchArgs.Parse(new[] { "--safe-mode", "--x11" });
            Assert.True(AppLaunchArgs.SafeMode);
            Assert.True(AppLaunchArgs.ForceX11);
            Assert.False(AppLaunchArgs.ForceWayland);
            Assert.False(AppLaunchArgs.PreferGpu);

            AppLaunchArgs.Parse(new[] { "--wayland", "--gpu" });
            Assert.True(AppLaunchArgs.ForceWayland);
            Assert.True(AppLaunchArgs.PreferGpu);
            Assert.False(AppLaunchArgs.ForceX11);
            Assert.False(AppLaunchArgs.SafeMode);

            AppLaunchArgs.Parse(new[] { "--SAFE-MODE" });
            Assert.True(AppLaunchArgs.SafeMode);

            AppLaunchArgs.Parse(Array.Empty<string>());
            Assert.False(AppLaunchArgs.SafeMode);
            Assert.False(AppLaunchArgs.PreferGpu);
            Assert.False(AppLaunchArgs.ForceX11);
            Assert.False(AppLaunchArgs.ForceWayland);
        }
        finally
        {
            AppLaunchArgs.Parse(Array.Empty<string>());
        }
    }
}
