using System;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Verifies the Linux AppIndicator native bindings load and resolve their
/// symbols. Creating an actual indicator needs a GTK display + a StatusNotifier
/// host, which CI does not have, so only symbol resolution is checked here.
/// </summary>
public class AppIndicatorNativeTests
{
    [SkippableFact]
    public void AppIndicator_SymbolsResolve_WhenLibraryPresent()
    {
        if (!OperatingSystem.IsLinux()) Skip.If(true, "Linux only");
        Skip.IfNot(AppIndicatorNative.IsAvailable, "libayatana-appindicator3/libappindicator3 is not installed");

        Assert.True(AppIndicatorNative.TryLoad(out var error), error);

        Assert.NotNull(AppIndicatorNative.AppIndicatorNew);
        Assert.NotNull(AppIndicatorNative.AppIndicatorSetStatus);
        Assert.NotNull(AppIndicatorNative.AppIndicatorSetMenu);
        Assert.NotNull(AppIndicatorNative.AppIndicatorSetIconFull);
        Assert.NotNull(AppIndicatorNative.GtkMenuNew);
        Assert.NotNull(AppIndicatorNative.GtkMenuItemNewWithLabel);
        Assert.NotNull(AppIndicatorNative.GtkMenuShellAppend);
        Assert.NotNull(AppIndicatorNative.GtkWidgetShowAll);
        Assert.NotNull(AppIndicatorNative.GSignalConnectData);
        Assert.NotNull(AppIndicatorNative.GObjectUnref);
    }

    [SkippableFact]
    public void TrayFactory_IsSupported_MatchesAppIndicatorAvailability_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) Skip.If(true, "Linux only");

        Assert.Equal(AppIndicatorNative.IsAvailable, TrayIconFactory.IsSupported);
    }
}
